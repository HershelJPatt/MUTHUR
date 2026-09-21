<#
.SYNOPSIS
Exercises an installed T-100 build using only its CLI and HTTP, a scratch home and local git fixture.
.EXAMPLE
./scripts/Test-T100Capabilities.ps1 -Install C:/scratch/muthur-T-100 -Revision <candidate-sha>
#>
param(
    [Parameter(Mandatory)][string]$Install,
    [Parameter(Mandatory)][string]$Revision,
    [string]$EvidencePath = (Join-Path $PWD ('artifacts/T-100-evidence-' + [guid]::NewGuid().ToString('n') + '.json'))
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Install)) { throw 'Install must be absolute.' }
$installation = [IO.Path]::GetFullPath($Install)
$cli = Join-Path $installation $(if ($IsWindows) { 'muthur.exe' } else { 'muthur' })
if (-not [IO.File]::Exists($cli) -or -not [IO.Directory]::Exists((Join-Path $installation 'server'))) {
    throw 'Install must name an installed CLI with its bundled server, not development output.'
}
$scratch = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('muthur-T-100-' + [guid]::NewGuid().ToString('n'))))
$scratchHome = Join-Path $scratch 'home'
$repo = Join-Path $scratch 'repo'
$envNames = @('MUTHUR_HOME', 'MUTHUR_URL', 'MUTHUR_AGENT', 'MUTHUR_TOKEN', 'MUTHUR_SERVER',
    'Muthur__BackgroundServices', 'Muthur__DataDir', 'Muthur__ConnectionString', 'Muthur__DbProvider',
    'Muthur__Url', 'ASPNETCORE_URLS', 'PATH', 'CODEX_HOME', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_COUNT', 'T100_STARTS')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$outcomes = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$hubProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$hubStartAttempted = $false
$evidence = [ordered]@{
    task = 'T-100'; cohort = 'installed-simulated-capability-fixture'; revision = $Revision; sampleCount = 1; realModelStarts = 0; startedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installPath = $installation; status = 'running'; commands = $outcomes; cleanupFailures = $cleanup
    livePilot = @{ status = 'missing'; reason = 'Next eligible post-install real task requires an orchestrator-linked observation follow-up.' }
}

function Run-Process([string]$Executable, [string[]]$Arguments, [string]$Directory, [int]$TimeoutSeconds = 150) {
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
    finally { $process.Dispose() }
}

function Invoke-T100Cli([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ExpectedCode = '') {
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
    $null = Invoke-T100Cli @('up')
    $status = Invoke-T100Cli @('status')
    Check ([IO.Path]::GetFullPath($status.dataDirectory) -eq [IO.Path]::GetFullPath($scratchHome)) 'hub uses unique scratch home'
    $process = [Diagnostics.Process]::GetProcessById([int]$status.processId)
    $hubProcesses.Add($process)
    return $status
}

function ProbeApi([string]$Action, [hashtable]$Body) {
    # The private installed hub's own identity; never print credentials or contact a live hub.
    $token = [IO.File]::ReadAllText((Join-Path $scratchHome 'founder.token')).Trim()
    Invoke-RestMethod -Uri "$env:MUTHUR_URL/api/v1/workers/probes/$Action" -Method Post -ContentType 'application/json' -Headers @{Authorization="Bearer $token"} -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 15
}
function Starts {
    if (Test-Path -LiteralPath $env:T100_STARTS) { return @(Get-Content -LiteralPath $env:T100_STARTS).Count }
    return 0
}
try {
    if ($Revision -notmatch '^[0-9a-f]{40}$') { throw 'Revision must be the full candidate SHA.' }
    $bin = Join-Path $scratch 'bin'
    $null = New-Item -ItemType Directory -Path $repo,$scratchHome,$bin,(Join-Path $scratch 'codex') -Force
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $env:MUTHUR_HOME = $scratchHome
    $env:MUTHUR_URL = "http://127.0.0.1:$port"
    $env:ASPNETCORE_URLS = $env:MUTHUR_URL
    $env:Muthur__Url = $env:MUTHUR_URL
    $env:Muthur__DataDir = $scratchHome
    $env:Muthur__ConnectionString = 'Data Source="' + (Join-Path $scratchHome 'muthur.db') + '";Pooling=False'
    $env:Muthur__DbProvider = 'sqlite'; $env:Muthur__BackgroundServices = 'false'
    $env:MUTHUR_AGENT = $null; $env:MUTHUR_TOKEN = $null; $env:MUTHUR_SERVER = $null
    $env:CODEX_HOME = Join-Path $scratch 'codex'
    $env:GIT_CONFIG_GLOBAL = if ($IsWindows) { 'NUL' } else { '/dev/null' }
    $env:GIT_CONFIG_NOSYSTEM = '1'; $env:GIT_CONFIG_COUNT = $null
    $env:T100_STARTS = Join-Path $scratch 'starts.txt'
    $env:PATH = $bin + [IO.Path]::PathSeparator + $env:PATH
    $fake = Join-Path $bin 'fixture.ps1'
    @'
$ErrorActionPreference = 'Stop'
if ($args -contains '--version') { Write-Output 'T100-simulated-codex-v1'; exit 0 }
$sandboxIndex = [Array]::IndexOf($args, '--sandbox')
if ($sandboxIndex -lt 0 -or $args[$sandboxIndex + 1] -ne 'workspace-write') { throw 'Unexpected permission mode.' }
if ($args -contains '--dangerously-bypass-approvals-and-sandbox') { throw 'Permission expansion is forbidden.' }
[IO.File]::AppendAllText($env:T100_STARTS, "start`n")
$prompt = [Console]::In.ReadToEnd()
$commands = Join-Path $PWD '.muthur-capability/commands.json'
if (Test-Path -LiteralPath $commands) {
    foreach ($step in (Get-Content -Raw -LiteralPath $commands | ConvertFrom-Json)) {
        & pwsh -NoProfile -Command ($step.command + "`n" + $step.receipt) | Out-Null
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
} else {
    $index = [Array]::IndexOf($args, '--output-last-message')
    if ($index -lt 0) { throw 'Missing adapter output file.' }
    [IO.File]::WriteAllText($args[$index + 1], "STATUS: done`nSUMMARY: Simulated installed assignment; no product changes.`n")
}
exit 0
'@ | Set-Content -LiteralPath $fake
    if ($IsWindows) {
        [IO.File]::WriteAllText((Join-Path $bin 'codex.cmd'), "@echo off`r`n`"$((Get-Command pwsh).Source)`" -NoProfile -File `"$fake`" %*`r`n")
    } else {
        [IO.File]::WriteAllText((Join-Path $bin 'codex'), "#!/bin/sh`nexec pwsh -NoProfile -File '$fake' " + '"$@"' + "`n")
        $chmod = Run-Process 'chmod' @('+x', (Join-Path $bin 'codex')) $repo
        Check ($chmod.Exit -eq 0) 'fixture executable permission'
    }
    $null = Git @('init','-q','-b','main')
    $null = Git @('config','user.name','Capability fixture')
    $null = Git @('config','user.email','fixture@example.invalid')
    $null = Git @('config','commit.gpgsign','false')
    $null = New-Item -ItemType Directory (Join-Path $repo 'specs')
    [IO.File]::WriteAllText((Join-Path $repo 'specs/T-1.md'), "# T-1 simulated assignment`ncapabilities: shell, build, test, worktree-base, commit`n")
    $null = Git @('add','.'); $null = Git @('commit','-q','-m','Pinned capability fixture')
    $null = Git @('branch','task/T-1')
    $base = Git @('rev-parse','HEAD')
    $status = Start-ScratchHub
    $evidence.serverRevision = $status.version
    $evidence.url = $env:MUTHUR_URL
    $version = Run-Process $cli @('--version') $repo
    $evidence.cliRevision = $version.Out.Trim()
    Check ($version.Exit -eq 0 -and $version.Out.Contains($Revision.Substring(0,7))) 'installed candidate revision'
    @{tiers=@{implementer=@(@{harness='codex';model='fixture';account='fixture'});mastermind=@()}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $scratchHome 'harnesses.json')
    $null = Invoke-T100Cli @('project','add','capability-fixture','--repo',$repo,'--founder')
    $task = Invoke-T100Cli @('task','add','Simulated capability assignment','--project','capability-fixture','--founder')
    Check ($task.id -eq 'T-1') 'isolated task numbering'
    $common = @('--spec','specs/T-1.md','--base','task/T-1','--default-branch','main','--harness','codex','--launch-path','worker-run')
    $unknown = Invoke-T100Cli (@('capability','inspect') + $common)
    Check ($null -ne $unknown.identity -and -not $unknown.match.allowed -and @($unknown.match.missing | Where-Object state -ne 'unknown').Count -eq 0) 'unknown exact identity'
    $null = Invoke-T100Cli @('conductor','off','--founder')
    $refused = Invoke-T100Cli (@('capability','probe','--task','T-1','--founder') + $common) 2
    Check (($refused | ConvertTo-Json -Depth 10).Contains('probe_admission_disabled') -and (Starts) -eq 0) 'admission refusal has zero simulated starts'
    $null = Invoke-T100Cli @('conductor','on','--founder')
    $request = @{task='T-1';tier='implementer';harness='codex';model='fixture';account='fixture';runId=[guid]::NewGuid().ToString('N')}
    $granted = ProbeApi 'admit' $request
    Check $granted.mayExecute 'one reservation granted'
    $replay = ProbeApi 'admit' $request
    Check (-not $replay.mayExecute -and $replay.reservationId -eq $granted.reservationId) 'replay cannot execute'
    $null = ProbeApi 'release' @{reservationId=$granted.reservationId}
    $null = ProbeApi 'release' @{reservationId=$granted.reservationId}
    Check (-not (ProbeApi 'admit' $request).mayExecute -and (Starts) -eq 0) 'release is idempotent and does not reactivate'
    $probe = Invoke-T100Cli (@('capability','probe','--task','T-1','--timeout-seconds','120','--founder') + $common)
    Check ($probe.probeStarts -eq 1 -and (Starts) -eq 1 -and @($probe.observations | Where-Object state -ne 'available').Count -eq 0) 'simulated admitted engine verifies each real SDK fixture command'
    $evidence.probeStarts = $probe.probeStarts; $evidence.probeMilliseconds = $probe.durationMilliseconds
    $available = Invoke-T100Cli (@('capability','inspect') + $common)
    Check $available.match.allowed 'same exact identity matches engine evidence'
    $cache = @(Get-ChildItem -LiteralPath (Join-Path $scratchHome 'capabilities') -Filter '*.json')
    Check ($cache.Count -eq 1) 'one exact identity cache'
    $original = [IO.File]::ReadAllText($cache[0].FullName)
    foreach ($state in @('stale','unavailable')) {
        $records = $original | ConvertFrom-Json
        foreach ($record in $records) {
            if ($state -eq 'stale') { $expired=[DateTimeOffset]::UtcNow.AddDays(-2); $record.observedAt=$expired.ToString('O'); $record.expiresAt=$expired.AddHours(24).ToString('O') }
            else { $record.state='unavailable' }
            $record.evidence='Explicitly fixture-seeded simulated observation; never live evidence.'
        }
        ConvertTo-Json -InputObject @($records) -Depth 12 | Set-Content -LiteralPath $cache[0].FullName
        $inspection = Invoke-T100Cli (@('capability','inspect') + $common)
        Check (-not $inspection.match.allowed -and @($inspection.match.missing | Where-Object state -ne $state).Count -eq 0) "$state refuses match"
    }
    [IO.File]::WriteAllText($cache[0].FullName, $original)
    $worker = Invoke-T100Cli @('worker','run','--spec','specs/T-1.md','--task','T-1','--base','task/T-1','--default-branch','main','--harness','codex','--branch','worker/T-1-smoke','--founder')
    Check ($worker.success -and $worker.fullStarts -eq 1 -and $worker.fullSessionsAvoided -eq 0 -and (Starts) -eq 2) 'simulated supported assignment end to end'
    $evidence.fullStarts = $worker.fullStarts
    Check ((Git @('rev-parse','task/T-1')) -eq $base -and (Git @('rev-parse','main')) -eq $base) 'probe preserved task and default refs'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $scratchHome 'capability-scratch') -Directory).Count -eq 0) 'probe scratch cleaned before release'
    $conductor = Invoke-T100Cli @('conductor','status')
    Check ($conductor.running -eq 0) 'zero active reservations or sessions'
    $evidence.status = 'passed'
}

catch {
    $evidence.status = 'failed'
    $evidence.error = $_.Exception.Message
}
finally {
    try {
        if ($hubStartAttempted -and [IO.Directory]::Exists($scratchHome)) {
            $pidPath = Join-Path $scratchHome 'muthur.pid'
            if ([IO.File]::Exists($pidPath)) {
                $scratchPid = [int][IO.File]::ReadAllText($pidPath)
                if (-not ($hubProcesses | Where-Object Id -eq $scratchPid)) {
                    $hubProcesses.Add([Diagnostics.Process]::GetProcessById($scratchPid))
                }
            }
            $null = Invoke-T100Cli @('down')
        }
    }
    catch { $cleanup.Add('Graceful scratch shutdown: ' + $_.Exception.Message) }
    foreach ($process in $hubProcesses) {
        try {
            if (-not $process.HasExited) {
                if (-not $process.WaitForExit(5000)) {
                    $process.Kill($true)
                    if (-not $process.WaitForExit(5000)) { throw 'Scratch process did not stop after termination.' }
                }
            }
        }
        catch { $cleanup.Add('Scratch process cleanup: ' + $_.Exception.Message) }
        finally { $process.Dispose() }
    }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    try {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('muthur-T-100-')) { throw 'Scratch cleanup containment check failed.' }
        if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
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

