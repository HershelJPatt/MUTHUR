<#
.SYNOPSIS
Exercises shared incidents using an installed CLI/server, a scratch home and local git fixture.
.EXAMPLE
./scripts/verify-T-103.ps1 -InstallPath C:/scratch/muthur-T-103
#>
param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$EvidencePath = (Join-Path $PWD ('artifacts/T-103-evidence-' + [guid]::NewGuid().ToString('n') + '.json'))
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'InstallPath must be absolute.' }
if (-not [IO.Path]::IsPathFullyQualified($EvidencePath)) { throw 'EvidencePath must be absolute.' }
$installation = [IO.Path]::GetFullPath($InstallPath)
$cli = Join-Path $installation $(if ($IsWindows) { 'muthur.exe' } else { 'muthur' })
if (-not [IO.File]::Exists($cli) -or -not [IO.Directory]::Exists((Join-Path $installation 'server'))) {
    throw 'InstallPath must name an installed CLI with its bundled server, not development output.'
}
$scratch = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('muthur-T-103-' + [guid]::NewGuid().ToString('n'))))
$scratchHome = Join-Path $scratch 'home'
$repo = Join-Path $scratch 'repo'
$envNames = @('MUTHUR_HOME', 'MUTHUR_URL', 'MUTHUR_AGENT', 'MUTHUR_TOKEN', 'MUTHUR_SERVER',
    'Muthur__BackgroundServices', 'Muthur__DataDir', 'Muthur__ConnectionString', 'Muthur__DbProvider',
    'Muthur__Url', 'ASPNETCORE_URLS')
$previous = @{}
$envNames = @($envNames + @([Environment]::GetEnvironmentVariables('Process').Keys | Where-Object { $_ -match '^(MUTHUR|ASPNETCORE_)' }) | Select-Object -Unique)
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$outcomes = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$hubProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$hubStartAttempted = $false
$evidence = [ordered]@{
    task = 'T-103'; cohort = 'installed-local-fixture'; startedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installPath = $installation; status = 'running'; commands = $outcomes; cleanupFailures = $cleanup
    livePilot = @{ status = 'missing'; reason = 'Next eligible post-install real task requires an orchestrator-linked observation follow-up.' }
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
    finally { $process.Dispose() }
}

function Invoke-T103Cli([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ExpectedCode = '') {
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
    $outcomes.Add(@{ command = 'git ' + ($Arguments -join ' '); exit = $result.Exit })
    if ($result.Exit -ne 0) { throw "Local fixture git failed: $($result.Error)" }
    return $result.Out.Trim()
}

function Check([bool]$Condition, [string]$Name) {
    $outcomes.Add(@{ check = $Name; passed = $Condition })
    if (-not $Condition) { throw "Check failed: $Name" }
}

function Start-ScratchHub {
    $script:hubStartAttempted = $true
    $null = Invoke-T103Cli @('up')
    $status = Invoke-T103Cli @('status')
    Check ([IO.Path]::GetFullPath($status.dataDirectory) -eq [IO.Path]::GetFullPath($scratchHome)) 'hub uses unique scratch home'
    $process = [Diagnostics.Process]::GetProcessById([int]$status.processId)
    $hubProcesses.Add($process)
    return $status
}

try {
    $null = New-Item -ItemType Directory -Path $repo, $scratchHome -Force
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
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
    $null = Git @('config', 'user.name', 'T-103 installed fixture')
    $null = Git @('config', 'user.email', 'T-103@example.invalid')
    $null = Git @('config', 'commit.gpgsign', 'false')
    $null = Git @('config', 'core.autocrlf', 'false')
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), "Local fixture only.`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'local fixture initial')
    $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $pin = Run-Process 'git' @('rev-parse', 'HEAD') $sourceRoot
    if ($pin.Exit -ne 0) { throw 'Cannot pin source revision.' }
    $evidence.sourceRevision = $pin.Out.Trim()
    $evidence.cliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash
    $evidence.serverSha256 = (Get-FileHash -LiteralPath (Join-Path $installation 'server/Muthur.Server.dll') -Algorithm SHA256).Hash
    $status = Start-ScratchHub
    $evidence.serverRevision = $status.version
    $null = Invoke-T103Cli @('project', 'add', 'incident-fixture', '--repo', $repo, '--founder')
    $null = Invoke-T103Cli @('agent', 'register', '--name', 'incident-owner', '--harness', 'local', '--model', 'fixture')
    $tasks = @()
    foreach ($title in @('First independent observation', 'Second independent observation', 'Unaffected task', 'Human gated task')) {
        $tasks += Invoke-T103Cli @('task', 'add', $title, '--project', 'incident-fixture', '--as-agent', 'incident-owner')
    }
    $first, $second, $third, $human = $tasks
    $null = Invoke-T103Cli @('task', 'attended', $human.id, '--reason', 'Human measurement', '--founder')
    $null = Invoke-T103Cli @('task', 'claim', $human.id, '--as-agent', 'incident-owner')
    $request = Invoke-T103Cli @('ask', 'Approve this scratch product choice', '--kind', 'human', '--task', $human.id, '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('task', 'dependencies', $human.id, '--after', $third.id, '--reason', 'Independent prerequisite', '--founder')
    $incident = Invoke-T103Cli @('incident', 'add', 'Measured shared failure', '--project', 'incident-fixture', '--signature', 'exact?failure&v=1', '--path', 'local/worker', '--configuration', 'fixture-v1', '--recovery', 'Successful bounded local probe', '--as-agent', 'incident-owner')
    $id = $incident.id
    $match = Invoke-T103Cli @('incident', 'match', '--project', 'incident-fixture', '--signature', 'exact?failure&v=1', '--path', 'local/worker', '--configuration', 'fixture-v1')
    Check ($match.advisory -and $match.matches.Count -eq 1) 'exact escaped advisory match'
    $observations = @()
    foreach ($task in @($first, $second)) {
        $observations += Invoke-T103Cli @('incident', 'observe', $id, '--task', $task.id, '--evidence', ('Independent observation for ' + $task.id), '--signature', 'exact?failure&v=1', '--path', 'local/worker', '--configuration', 'fixture-v1', '--as-agent', 'incident-owner')
    }
    Check ($observations[0].id -ne $observations[1].id) 'independent observations retained'
    $null = Invoke-T103Cli @('incident', 'update', $id, '--diagnosis', 'Shared diagnosis recorded once', '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('incident', 'transition', $id, '--state', 'confirmed', '--evidence', 'Compared independent exact measurements', '--as-agent', 'incident-owner')
    for ($index = 0; $index -lt 2; $index++) {
        $task = $tasks[$index]
        $null = Invoke-T103Cli @('incident', 'suppress', $id, '--task', $task.id, '--assignment', '#orchestrator', '--observation', [string]$observations[$index].id, '--reason', 'Exact measured condition unchanged', '--as-agent', 'incident-owner')
    }
    $token = [IO.File]::ReadAllText((Join-Path $scratchHome 'founder.token')).Trim()
    $conductor = Invoke-RestMethod "$env:MUTHUR_URL/api/v1/conductor" -TimeoutSec 10
    $outcomes.Add(@{ command = 'GET /api/v1/conductor'; status = 200 })
    Check ($conductor.incidentSuppressions.Count -eq 2) 'status explains both gates'
    $null = Invoke-T103Cli @('task', 'claim', $first.id, '--as-agent', 'incident-owner') 3 'incident_wait'
    $null = Invoke-T103Cli @('task', 'claim', $third.id, '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('task', 'cancel', $third.id, '--reason', 'Retain terminal state', '--founder')
    $null = Invoke-T103Cli @('down')
    foreach ($process in $hubProcesses) {
        if (-not $process.HasExited -and -not $process.WaitForExit(10000)) { throw 'Scratch restart shutdown timed out.' }
    }
    $null = Start-ScratchHub
    $restored = Invoke-T103Cli @('incident', 'show', $id)
    Check (@($restored.suppressions | Where-Object effective).Count -eq 2) 'persisted suppressions survive installed restart'
    $null = Invoke-T103Cli @('incident', 'unlink', $id, '--observation', [string]$observations[0].id, '--reason', 'Explicit grouping correction', '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('task', 'claim', $first.id, '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('task', 'claim', $second.id, '--as-agent', 'incident-owner') 3 'incident_wait'
    $null = Invoke-T103Cli @('incident', 'recover', $id, '--kind', 'configuration', '--configuration', 'fixture-v1', '--evidence', 'Unchanged measurement', '--as-agent', 'incident-owner') 2 'incident_input'
    $null = Invoke-T103Cli @('incident', 'recover', $id, '--kind', 'probe', '--configuration', 'fixture-v2', '--evidence', 'Invalid flags', '--as-agent', 'incident-owner') 2 'incident_input'
    $bad = Invoke-WebRequest "$env:MUTHUR_URL/api/v1/incidents/$id/recover" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body '{"kind":"probe","evidence":" "}' -SkipHttpErrorCheck -TimeoutSec 10
    $outcomes.Add(@{ command = "POST /api/v1/incidents/$id/recover (blank evidence)"; status = [int]$bad.StatusCode })
    Check ($bad.StatusCode -eq 422 -and ($bad.Content | ConvertFrom-Json).code -eq 'incident_evidence') 'API rejects blank recovery evidence'
    $null = Invoke-T103Cli @('incident', 'recover', $id, '--kind', 'probe', '--evidence', 'Successful bounded scratch probe for exact fixture condition', '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('task', 'claim', $second.id, '--as-agent', 'incident-owner')
    $null = Invoke-T103Cli @('incident', 'suppress', $id, '--task', $second.id, '--assignment', '#orchestrator', '--observation', [string]$observations[1].id, '--reason', 'Stale evidence', '--as-agent', 'incident-owner') 2 'incident_evidence'
    $humanAfter = Invoke-T103Cli @('task', 'show', $human.id)
    Check ($humanAfter.task.state -eq 'blocked' -and $humanAfter.task.attendedReason -eq 'Human measurement' -and $humanAfter.task.dependsOn -contains $third.id) 'human gate and dependency remain intact'
    $requests = @(Invoke-T103Cli @('requests', 'list', '--founder'))
    Check (@($requests | Where-Object id -eq $request.id).Count -eq 1) 'human request remains open'
    $terminal = Invoke-T103Cli @('task', 'show', $third.id)
    Check ($terminal.task.state -eq 'cancelled') 'terminal task remains terminal'
    $history = Invoke-T103Cli @('incident', 'show', $id)
    $metrics = Invoke-T103Cli @('incident', 'metrics', $id, '--hours', '24')
    Check (([string]$metrics.implementationRevision).EndsWith('+' + $evidence.sourceRevision, [StringComparison]::OrdinalIgnoreCase)) 'installed server matches pinned source revision'
    Check ($history.observations.Count -eq 2 -and $history.incident.conditionVersion -eq 2 -and $history.incident.state -eq 'mitigated') 'history and condition version retained'
    Check ($metrics.observationCount -eq 2 -and $metrics.recordedDiagnosisUpdates -eq 1 -and $metrics.groupingCorrectionReleaseCount -eq 1) 'metrics report measured counts'
    Check ($null -eq $metrics.actualProcessStarts -and $metrics.conductorStaffingAttempts -eq 0) 'no real workers launched or process attribution invented'
    $evidence.metrics = $metrics
    $evidence.window = @{ from = $metrics.windowStart; to = $metrics.windowEnd; sourceEventSequenceIds = $metrics.sourceEventSequenceIds }
    $evidence.cohort = @($first.id, $second.id)
    $evidence.sampleCounts = @{ tasks = 4; observations = 2; suppressions = 2; recoveries = 1 }
    $pinAfter = Run-Process 'git' @('rev-parse', 'HEAD') $sourceRoot
    Check ($pinAfter.Exit -eq 0 -and $pinAfter.Out.Trim() -eq $evidence.sourceRevision) 'source revision remained pinned'
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
            $null = Invoke-T103Cli @('down')
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
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('muthur-T-103-')) { throw 'Scratch cleanup containment check failed.' }
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
