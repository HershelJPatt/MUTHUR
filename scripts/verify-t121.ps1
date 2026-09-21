param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$EvidencePath = (Join-Path $PWD ('artifacts/T-121-evidence-' + [guid]::NewGuid().ToString('n') + '.json'))
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'InstallPath must be absolute.' }
$installation = [IO.Path]::GetFullPath($InstallPath)
$cli = Join-Path $installation $(if ($IsWindows) { 'muthur.exe' } else { 'muthur' })
if (-not [IO.File]::Exists($cli) -or -not [IO.Directory]::Exists((Join-Path $installation 'server'))) {
    throw 'InstallPath must name an installed CLI with its bundled server, not development output.'
}
$scratch = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('muthur-T-121-' + [guid]::NewGuid().ToString('n'))))
$scratchHome = Join-Path $scratch 'home'
$repo = Join-Path $scratch 'repo'
$envNames = @('MUTHUR_HOME', 'MUTHUR_URL', 'MUTHUR_AGENT', 'MUTHUR_TOKEN', 'MUTHUR_SERVER',
    'Muthur__BackgroundServices', 'Muthur__DataDir', 'Muthur__ConnectionString', 'Muthur__DbProvider',
    'Muthur__Url', 'ASPNETCORE_URLS', 'PATH', 'T121_FIXTURE', 'T121_TRACE', 'T121_CRASH_EVENT')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$outcomes = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$hubProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$hubStartAttempted = $false
$evidence = [ordered]@{
    task = 'T-121'; cohort = 'installed-local-fixture'; startedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installPath = $installation; status = 'running'; commands = $outcomes; cleanupFailures = $cleanup
    syntheticOnly = $true
}

function Run-Process([string]$Executable, [string[]]$Arguments, [string]$Directory, [int]$TimeoutSeconds = 60) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw "Process did not stop after timeout: $Executable" }
            throw "Timed out: $Executable $($Arguments -join ' ')"
        }
        return @{ Exit = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); if (-not $process.WaitForExit(10000)) { throw 'Fixture process cleanup failed' } }
        $process.Dispose()
    }
}

function Invoke-T121Cli([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ExpectedCode = '') {
    $result = Run-Process $cli $Arguments $repo
    $body = if ($result.Exit -eq 0) { $result.Out } else { $result.Error }
    $value = if ([string]::IsNullOrWhiteSpace($body)) { @{} } else { $body | ConvertFrom-Json -AsHashtable }
    $outcomes.Add(@{ command = 'muthur ' + ($Arguments -join ' '); exit = $result.Exit; code = $value.code })
    if ($result.Exit -ne $ExpectedExit -or ($ExpectedCode -and $value.code -ne $ExpectedCode)) {
        throw "Unexpected CLI outcome: $($Arguments -join ' '): exit $($result.Exit), $body"
    }
    return $value
}

function Git([string[]]$Arguments) {
    $result = Run-Process 'git' $Arguments $repo
    if ($result.Exit -ne 0) { throw "Local fixture git failed: $($result.Error)" }
    return $result.Out.Trim()
}

function Check([bool]$Condition, [string]$Name) {
    $outcomes.Add(@{ check = $Name; passed = $Condition })
    if (-not $Condition) { throw "Check failed: $Name" }
}

function Start-ScratchHub {
    $script:hubStartAttempted = $true
    $null = Invoke-T121Cli @('up')
    $status = Invoke-T121Cli @('status')
    Check ([IO.Path]::GetFullPath($status.dataDirectory) -eq [IO.Path]::GetFullPath($scratchHome)) 'hub uses unique scratch home'
    $process = [Diagnostics.Process]::GetProcessById([int]$status.processId)
    $hubProcesses.Add($process)
    return $status
}

try {
    $null = New-Item -ItemType Directory -Path $repo, $scratchHome -Force
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $env:MUTHUR_HOME = $scratchHome
    $env:MUTHUR_URL = "http://127.0.0.1:$port"
    $env:ASPNETCORE_URLS = $env:MUTHUR_URL
    # Configuration-section overrides take precedence over the MUTHUR_* defaults.
    $env:Muthur__Url = $env:MUTHUR_URL
    $env:Muthur__DataDir = $scratchHome
    $env:Muthur__DbProvider = 'sqlite'
    $env:Muthur__ConnectionString = 'Data Source="' + (Join-Path $scratchHome 'muthur.db').Replace('"', '""') + '";Pooling=False'
    $env:Muthur__BackgroundServices = 'false'
    $env:MUTHUR_AGENT = $null
    $env:MUTHUR_TOKEN = $null
    $env:MUTHUR_SERVER = $null
    $evidence.url = $env:MUTHUR_URL
    $revision = Run-Process $cli @('--version') $repo
    $evidence.cliRevision = $revision.Out.Trim()
    $null = Git @('init', '-q', '-b', 'main')
    $null = Git @('config', 'user.name', 'T-121 installed fixture')
    $null = Git @('config', 'user.email', 'T-121@example.invalid')
    $null = Git @('config', 'commit.gpgsign', 'false')
    $null = Git @('config', 'core.autocrlf', 'false')
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), "Local fixture only.`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'local fixture initial')
    $status = Start-ScratchHub
    $evidence.serverRevision = $status.version

    $null = Invoke-T121Cli @('project', 'add', 'worker-fixture', '--repo', $repo, '--founder')
    $null = Invoke-T121Cli @('agent', 'register', '--name', 'worker-owner', '--harness', 'fixture', '--model', 'fixture', '--tier', 'mastermind')
    $null = Invoke-T121Cli @('conductor', 'on', '--founder')
    $null = Invoke-T121Cli @('conductor', 'sessions', '2', '--founder')
    $fakeBin = Join-Path $scratch 'fake-bin'
    $null = New-Item -ItemType Directory -Path $fakeBin
    $shell = (Get-Command pwsh).Source
    $env:T121_FIXTURE = Join-Path $fakeBin 'fixture.ps1'
    $env:T121_TRACE = Join-Path $scratch 'launches.txt'
    @'
if ($env:MUTHUR_TOKEN -or $env:MUTHUR_AGENT) { throw 'Hub identity reached synthetic worker' }
$null = [Console]::In.ReadToEnd()
$PID | Add-Content -LiteralPath $env:T121_TRACE
if ($env:T121_CRASH_EVENT) {
    $ready = [Threading.EventWaitHandle]::OpenExisting($env:T121_CRASH_EVENT)
    $ready.Set() | Out-Null
    [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null
}
if ($args -contains 'quota-model') { Write-Output '{"result":"usage limit reached","is_error":true}'; exit 1 }
Set-Content -LiteralPath 'synthetic-output.txt' -Value 'Synthetic worker fixture, no model was invoked.'
Write-Output '{"result":"STATUS: done","is_error":false}'
'@ | Set-Content -LiteralPath $env:T121_FIXTURE
    ('@echo off' + "`r`n" + '"' + $shell + '" -NoProfile -File "%T121_FIXTURE%" %*') | Set-Content -LiteralPath (Join-Path $fakeBin 'claude.cmd')
    $env:PATH = $fakeBin + [IO.Path]::PathSeparator + $env:PATH
    '{"tiers":{"implementer":[{"harness":"claude","model":"quota-model","account":"fixture-a"},{"harness":"claude","model":"done-model","account":"fixture-b"}]}}' |
        Set-Content -LiteralPath (Join-Path $scratchHome 'harnesses.json')
    '# Legacy spec with no capability declarations. Write the synthetic output.' | Set-Content -LiteralPath (Join-Path $repo 'spec.md')
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'frozen synthetic worker spec')
    $task = Invoke-T121Cli @('task', 'add', 'Full-worker installed fixture', '--project', 'worker-fixture', '--as-agent', 'worker-owner')
    $id = $task.id
    $null = Invoke-T121Cli @('task', 'claim', $id, '--as-agent', 'worker-owner')
    $null = Invoke-T121Cli @('worker', 'run', '--spec', 'spec.md', '--base', 'main', '--default-branch', 'main', '--branch', 'worker/no-task', '--as-agent', 'worker-owner') 2 'worker_request_invalid'
    Check (-not (Test-Path (Join-Path $repo '.worktrees'))) 'missing task has no worktree side effects'
    $run = Invoke-T121Cli @('worker', 'run', '--task', $id, '--spec', 'spec.md', '--base', 'main', '--default-branch', 'main', '--branch', 'worker/fixture', '--as-agent', 'worker-owner')
    Check ($run.success -and $run.fullStarts -eq 2 -and $run.attempts.Count -eq 2) 'legacy full-worker fallback launches two synthetic attempts'
    Check ($run.attempts[0].reservationId -ne $run.attempts[1].reservationId) 'fallback has separate reservations'
    Check (@($run.attempts | Where-Object cleanup -ne 'ExitedAndTreeEmpty').Count -eq 0) 'each fallback tree confirmed empty'
    $reservations = @(Invoke-T121Cli @('worker', 'reservations', '--as-agent', 'worker-owner'))
    Check ($reservations.Count -eq 0) 'successful CLI releases capacity'
    $events = @(Invoke-T121Cli @('log', '--task', $id, '--limit', '2000'))
    Check (@($events | Where-Object type -eq 'worker.admitted').Count -eq 2) 'two admissions, no report double charge'
    Check (@($events | Where-Object type -eq 'worker.released').Count -eq 2) 'two cleanup releases'

    # Direct installed HTTP exercises replay, budget, restart, conflict and explicit recovery.
    $headers = @{ Authorization = 'Bearer ' + [IO.File]::ReadAllText((Join-Path $scratchHome 'founder.token')).Trim() }
    function Api([string]$Path, [hashtable]$Body, [int]$Expected = 200) {
        $response = Invoke-WebRequest -Uri ($env:MUTHUR_URL + '/api/v1/' + $Path) -Method Post -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -SkipHttpErrorCheck -TimeoutSec 15
        Check ([int]$response.StatusCode -eq $Expected) "HTTP $Path status $Expected"
        if ($response.Content) { return $response.Content | ConvertFrom-Json -AsHashtable }
    }
    $request = @{ task=$id; tier='implementer'; harness='claude'; model='done-model'; account='fixture-b'; runId=[guid]::NewGuid().ToString('n'); inputHash=('a' * 64) }
    $grant = Api 'workers/admit' $request
    Check $grant.mayExecute 'last daily attempt admitted'
    $capacityTask = Invoke-T121Cli @('task', 'add', 'Synthetic capacity fixture', '--project', 'worker-fixture', '--as-agent', 'worker-owner')
    $extra = $request.Clone(); $extra.task = $capacityTask.id; $extra.runId = [guid]::NewGuid().ToString('n')
    $extraGrant = Api 'workers/admit' $extra
    $excess = $extra.Clone(); $excess.runId = [guid]::NewGuid().ToString('n')
    Check ((Api 'workers/admit' $excess 422).code -eq 'worker_capacity_exhausted') 'installed shared capacity denies excess worker'
    $probe = $excess.Clone(); $probe.Remove('inputHash')
    Check ((Api 'workers/probes/admit' $probe 422).code -eq 'probe_capacity_exhausted') 'worker reservations occupy probe capacity'
    $null = Api 'workers/release' @{ reservationId=$extraGrant.reservationId; cleanupConfirmed=$true } 204
    $replay = Api 'workers/admit' $request
    Check (-not $replay.mayExecute -and $replay.reservationId -eq $grant.reservationId) 'active replay never grants execution'
    $changed = $request.Clone(); $changed.inputHash = 'b' * 64
    Check ((Api 'workers/admit' $changed 409).code -eq 'worker_run_conflict') 'changed input conflicts'
    $next = $request.Clone(); $next.runId = [guid]::NewGuid().ToString('n')
    Check ((Api 'workers/admit' $next 422).code -eq 'worker_budget_exhausted') 'full worker charges existing daily bucket'
    $null = Invoke-T121Cli @('down')
    $null = Start-ScratchHub
    $replay = Api 'workers/admit' $request
    Check (-not $replay.mayExecute -and $replay.reservationId -eq $grant.reservationId) 'restart preserves replay'
    $listed = @(Invoke-T121Cli @('worker', 'reservations', '--founder'))
    Check ($listed.Count -eq 1 -and $listed[0].reservationId -eq $grant.reservationId) 'restart retains occupied reservation'
    $null = Invoke-T121Cli @('worker', 'release', $grant.reservationId, '--founder') 2 'worker_cleanup_unconfirmed'
    $null = Invoke-T121Cli @('worker', 'release', $grant.reservationId, '--cleanup-confirmed', '--founder')
    $null = Invoke-T121Cli @('worker', 'release', $grant.reservationId, '--cleanup-confirmed', '--founder')
    Check (-not (Api 'workers/admit' $request).mayExecute) 'released replay never executes'
    Check ((Api 'workers/admit' $next 422).code -eq 'worker_budget_exhausted') 'release and restart do not refund daily spending'
    $denied = Run-Process $cli @('worker','run','--task',$id,'--spec','spec.md','--base','main','--default-branch','main','--branch','worker/denied','--as-agent','worker-owner') $repo
    Check ($denied.Exit -ne 0 -and -not (Test-Path (Join-Path $repo '.worktrees/worker-denied'))) 'budget denied CLI creates no worktree'
    Check (@(Get-Content -LiteralPath $env:T121_TRACE).Count -eq 2) 'denials and replay launch no synthetic processes'
    $limited = $extra.Clone(); $limited.model='quota-model'; $limited.account='fixture-a'; $limited.runId=[guid]::NewGuid().ToString('n')
    Check ((Api 'workers/admit' $limited 422).code -eq 'worker_account_limited') 'installed account denial is independent of daily budget'

    # Kill only the CLI, not its tree: the job handle closing must terminate the worker itself.
    $crashTask = Invoke-T121Cli @('task','add','Synthetic CLI death fixture','--project','worker-fixture','--as-agent','worker-owner')
    $null = Invoke-T121Cli @('task','claim',$crashTask.id,'--as-agent','worker-owner')
    $env:T121_CRASH_EVENT = 'Local\muthur-cli-death-' + [guid]::NewGuid().ToString('n')
    $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $env:T121_CRASH_EVENT)
    $crashInfo = [Diagnostics.ProcessStartInfo]::new($cli)
    $crashInfo.UseShellExecute=$false; $crashInfo.CreateNoWindow=$true; $crashInfo.WorkingDirectory=$repo
    $crashInfo.RedirectStandardOutput=$true; $crashInfo.RedirectStandardError=$true
    foreach ($arg in @('worker','run','--task',$crashTask.id,'--spec','spec.md','--base','main','--default-branch','main','--branch','worker/crash','--as-agent','worker-owner')) { $crashInfo.ArgumentList.Add($arg) }
    $crashed = [Diagnostics.Process]::Start($crashInfo)
    try {
        $crashOut=$crashed.StandardOutput.ReadToEndAsync(); $crashError=$crashed.StandardError.ReadToEndAsync()
        Check ($ready.WaitOne(30000)) 'synthetic worker reached running state before CLI death'
        $modelPid=[int](@(Get-Content -LiteralPath $env:T121_TRACE)[-1])
        $modelProcess=[Diagnostics.Process]::GetProcessById($modelPid)
        try {
            $crashed.Kill()
            Check ($crashed.WaitForExit(10000)) 'original CLI exited after forced death'
            Check ($modelProcess.WaitForExit(10000)) 'CLI death closed job and killed synthetic worker'
        }
        finally { $modelProcess.Dispose() }
        $retained=@(Invoke-T121Cli @('worker','reservations','--as-agent','worker-owner'))
        Check ($retained.Count -eq 1 -and $retained[0].request.task -eq $crashTask.id) 'CLI death retains durable reservation'
        $null = Invoke-T121Cli @('worker','release',$retained[0].reservationId,'--cleanup-confirmed','--as-agent','worker-owner')
    }
    finally {
        if (-not $crashed.HasExited) { $crashed.Kill($true); $null=$crashed.WaitForExit(10000) }
        $crashed.Dispose(); $ready.Dispose(); $env:T121_CRASH_EVENT=$null
    }
    foreach ($line in Get-Content -LiteralPath $env:T121_TRACE) {
        $process = Get-Process -Id ([int]$line) -ErrorAction SilentlyContinue
        Check ($null -eq $process) 'synthetic worker PID has exited'
    }
    $evidence.status = 'passed'
}
catch { $evidence.status = 'failed'; $evidence.error = $_.Exception.Message }
finally {
    try { if ($hubStartAttempted) { $null = Invoke-T121Cli @('down') } }
    catch { $cleanup.Add('Scratch shutdown: ' + $_.Exception.Message) }
    foreach ($process in $hubProcesses) {
        try {
            if (-not $process.HasExited -and -not $process.WaitForExit(5000)) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'Scratch hub remains alive' }
            }
        }
        catch { $cleanup.Add($_.Exception.Message) }
        finally { $process.Dispose() }
    }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    try {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('muthur-T-121-')) { throw 'Scratch containment failed' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    catch { $cleanup.Add('Scratch files: ' + $_.Exception.Message) }
    if ($cleanup.Count) { $evidence.status = 'failed' }
    $evidence.endedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $outputFile = [IO.Path]::GetFullPath($EvidencePath)
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputFile)) -Force
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputFile -Encoding utf8
    Write-Output $outputFile
}
if ($evidence.status -ne 'passed') { throw "Installed verification failed; see $outputFile" }

