<#
.SYNOPSIS
Exercises an installed T-99 build using only its CLI and HTTP, a scratch home and local git fixture.
.EXAMPLE
./scripts/verify-T-99.ps1 -InstallPath C:/scratch/muthur-T-99
#>
param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$EvidencePath = (Join-Path $PWD ('artifacts/T-99-evidence-' + [guid]::NewGuid().ToString('n') + '.json'))
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'InstallPath must be absolute.' }
$installation = [IO.Path]::GetFullPath($InstallPath)
$cli = Join-Path $installation $(if ($IsWindows) { 'muthur.exe' } else { 'muthur' })
if (-not [IO.File]::Exists($cli) -or -not [IO.Directory]::Exists((Join-Path $installation 'server'))) {
    throw 'InstallPath must name an installed CLI with its bundled server, not development output.'
}
$scratch = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('muthur-T-99-' + [guid]::NewGuid().ToString('n'))))
$scratchHome = Join-Path $scratch 'home'
$repo = Join-Path $scratch 'repo'
$envNames = @('MUTHUR_HOME', 'MUTHUR_URL', 'MUTHUR_AGENT', 'MUTHUR_TOKEN', 'MUTHUR_SERVER',
    'Muthur__BackgroundServices', 'Muthur__DataDir', 'Muthur__ConnectionString', 'Muthur__DbProvider',
    'Muthur__Url', 'ASPNETCORE_URLS')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$outcomes = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$hubProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$hubStartAttempted = $false
$evidence = [ordered]@{
    task = 'T-99'; cohort = 'installed-local-fixture'; startedAt = [DateTimeOffset]::UtcNow.ToString('O')
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

function Cli([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ExpectedCode = '') {
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
    $null = Cli @('up')
    $status = Cli @('status')
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
    $null = Git @('config', 'user.name', 'T-99 installed fixture')
    $null = Git @('config', 'user.email', 'T-99@example.invalid')
    $null = Git @('config', 'commit.gpgsign', 'false')
    $null = Git @('config', 'core.autocrlf', 'false')
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), "Local fixture only.`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'local fixture initial')
    $status = Start-ScratchHub
    $evidence.serverRevision = $status.version
    $first = @(Cli @('log', '--limit', '2000'))
    $firstSeq = if ($first.Count) { ($first | Measure-Object seq -Maximum).Maximum } else { 0 }
    $null = Cli @('project', 'add', 'subject-fixture', '--repo', $repo, '--validator', 'local-validator', '--founder')
    $null = Cli @('role', 'define', 'local-validator', '--validator', '--founder')
    $null = Cli @('agent', 'register', '--name', 'subject-owner', '--harness', 'local', '--model', 'fixture')
    $null = Cli @('agent', 'register', '--name', 'subject-validator', '--harness', 'local', '--model', 'fixture')
    $null = Cli @('role', 'take', 'local-validator', '--as-agent', 'subject-validator')
    $task = Cli @('task', 'add', 'Installed validation subject fixture', '--project', 'subject-fixture', '--as-agent', 'subject-owner')
    $id = $task.id
    $html = (Invoke-WebRequest "$env:MUTHUR_URL/tasks/$id" -TimeoutSec 10).Content
    Check ($html.Contains('unknown') -and $html.Contains('revalidate')) 'unknown provenance has explicit recovery help'
    $branch = "task/$id-subject"
    $null = Cli @('task', 'claim', $id, '--as-agent', 'subject-owner')
    $null = Git @('checkout', '-q', '-b', $branch)
    $null = New-Item -ItemType Directory -Path (Join-Path $repo 'specs') -Force
    [IO.File]::WriteAllText((Join-Path $repo "specs/$id.md"), "# $id — installed fixture`nCheck exact implementation provenance.`n")
    [IO.File]::WriteAllText((Join-Path $repo 'muthur.project.json'), '{"key":"subject-fixture","build":"git status --porcelain","test":"git show HEAD:implementation.txt"}')
    [IO.File]::WriteAllText((Join-Path $repo 'implementation.txt'), "A`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'implementation A')
    $null = Cli @('task', 'spec', $id, "specs/$id.md", '--branch', $branch, '--as-agent', 'subject-owner')
    $round = Cli @('task', 'implemented', $id, '--branch', $branch, '--as-agent', 'subject-owner')
    $claim = Cli @('validate', 'claim', $id, '--as', 'local-validator', '--as-agent', 'subject-validator')
    $subject = $claim.currentSubject.id
    $sha = $claim.currentSubject.implementationSha
    Check ($subject -eq $round.currentSubject.id) 'claim returns full current subject'
    $html = (Invoke-WebRequest "$env:MUTHUR_URL/tasks/$id" -TimeoutSec 10).Content
    Check ($html.Contains($sha) -and $html.Contains('Spec SHA-256') -and $html.Contains('missing')) 'prerendered SHA, spec digest and missing evidence'
    $null = Cli @('validate', 'pass', $id, '--as', 'local-validator', '--subject', $subject, '--as-agent', 'subject-validator') 2 'evidence_required'
    $token = [IO.File]::ReadAllText((Join-Path $scratchHome 'agents/subject-validator.token')).Trim()
    $empty = Invoke-WebRequest "$env:MUTHUR_URL/api/v1/tasks/$id/pass" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body (@{ validator = 'local-validator'; subjectId = $subject; evidence = '' } | ConvertTo-Json) -SkipHttpErrorCheck -TimeoutSec 10
    Check ($empty.StatusCode -eq 422 -and ($empty.Content | ConvertFrom-Json).code -eq 'evidence_required') 'empty pass API refusal'
    $report = Join-Path $scratch 'report.txt'
    Check ((Git @('show', "$sha`:implementation.txt")) -eq 'A') 'inspect exact implementation A'
    [IO.File]::WriteAllText($report, "Ran git show $sha`:implementation.txt; observed A. Reproduce with the same command.")
    $passed = Cli @('validate', 'pass', $id, '--as', 'local-validator', '--subject', $subject, '--evidence-file', $report, '--as-agent', 'subject-validator')
    Check ($passed.state -eq 'validated') 'valid pass'
    [IO.File]::WriteAllText((Join-Path $repo 'implementation.txt'), "B`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'unreviewed B')
    $null = Git @('checkout', '-q', 'main')
    $null = Cli @('task', 'land', $id, '--as-agent', 'subject-owner') 2 'implementation_changed'
    $null = Cli @('task', 'revalidate', $id, '--reason', 'Review B and the amended spec.', '--as-agent', 'subject-owner')
    $html = (Invoke-WebRequest "$env:MUTHUR_URL/tasks/$id" -TimeoutSec 10).Content
    Check ($html.Contains('stale') -and $html.Contains('revalidate')) 'invalidated provenance and evidence are stale'
    $null = Git @('checkout', '-q', $branch)
    [IO.File]::AppendAllText((Join-Path $repo "specs/$id.md"), "Spec-only amendment requires explicit attachment.`n")
    $null = Git @('add', '.')
    $null = Git @('commit', '-q', '-m', 'spec-only amendment')
    $null = Cli @('task', 'implemented', $id, '--branch', $branch, '--as-agent', 'subject-owner') 2 'spec_changed'
    $null = Cli @('task', 'spec', $id, "specs/$id.md", '--branch', $branch, '--as-agent', 'subject-owner')
    $fresh = Cli @('task', 'implemented', $id, '--branch', $branch, '--as-agent', 'subject-owner')
    $claim = Cli @('validate', 'claim', $id, '--as', 'local-validator', '--as-agent', 'subject-validator')
    Check ($claim.currentSubject.id -ne $subject) 'resubmission opens a new round'
    $null = Cli @('validate', 'pass', $id, '--as', 'local-validator', '--subject', $subject, '--evidence-file', $report, '--as-agent', 'subject-validator') 3 'stale_validation_subject'
    $subject = $claim.currentSubject.id
    $sha = $claim.currentSubject.implementationSha
    Check ((Git @('show', "$sha`:implementation.txt")) -eq 'B') 'inspect exact implementation B'
    [IO.File]::WriteAllText($report, "Ran git show $sha`:implementation.txt; observed B. Reproduce with the same command.")
    $null = Cli @('validate', 'pass', $id, '--as', 'local-validator', '--subject', $subject, '--evidence-file', $report, '--as-agent', 'subject-validator')
    $null = Cli @('down')
    $null = Start-ScratchHub
    $restored = Cli @('task', 'show', $id)
    Check ($restored.task.currentSubject.id -eq $subject -and $restored.task.validations[0].evidenceStatus -eq 'reported') 'subject and evidence persist across restart'
    $null = Git @('checkout', '-q', 'main')
    $landed = Cli @('task', 'land', $id, '--as-agent', 'subject-owner')
    Check ($landed.state -eq 'done' -and (Git @('rev-parse', 'main^2')) -eq $sha) 'exact approved commit landed'
    $html = (Invoke-WebRequest "$env:MUTHUR_URL/tasks/$id" -TimeoutSec 10).Content
    Check ($html.Contains($subject) -and $html.Contains('reported')) 'prerendered round and reported evidence after restart'
    $events = @(Cli @('log', '--since', "$firstSeq", '--limit', '2000'))
    $evidence.window = @{ firstExclusive = $firstSeq; lastInclusive = ($events | Measure-Object seq -Maximum).Maximum }
    $evidence.counts = @($events | Group-Object type | Sort-Object Name | ForEach-Object { @{ event = $_.Name; count = $_.Count } })
    $evidence.approved = @{ task = $id; subjectId = $subject; implementationSha = $sha; specSha256 = $fresh.currentSubject.specSha256 }
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
            $null = Cli @('down')
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
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('muthur-T-99-')) { throw 'Scratch cleanup containment check failed.' }
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
