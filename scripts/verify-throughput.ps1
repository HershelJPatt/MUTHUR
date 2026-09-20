# Installed-product test, no paid inference. Records phase timing; only its unique scratch hub is mutated.
param([Parameter(Mandatory)][string]$CliPath, [string]$Evidence = (Join-Path $PSScriptRoot ('../artifacts/t95-installed-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$Evidence = [IO.Path]::GetFullPath($Evidence)
if ((Test-Path -LiteralPath $Evidence) -and @(Get-ChildItem -LiteralPath $Evidence -Force).Count -gt 0) { throw 'Use a fresh evidence directory for each verification attempt.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('t95-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $scratch 'repo'
$server = Join-Path (Split-Path $CliPath) 'server/Muthur.Server.exe'
$saved = @{}
$phases = [Collections.Generic.List[object]]::new()
$hub = $null
$http = [Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(10)
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$url = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
$listener.Stop()
function Put([string]$file, [string]$text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
    [IO.File]::WriteAllText($file, $text)
}
function Check([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
function Child([string]$binary, [string[]]$arguments, [string]$directory) {
    $psi = [Diagnostics.ProcessStartInfo]::new($binary)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $directory
    foreach ($arg in $arguments) { $psi.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    Check $p.Start() 'Child start failed.'
    return $p
}
function Run([string]$binary, [string[]]$arguments, [string]$name, [int]$expected = 0) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Child $binary $arguments $repo
    try {
        $out = $p.StandardOutput.ReadToEndAsync()
        $err = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(60000)) { $p.Kill($true); $p.WaitForExit(); throw "$name timed out" }
        $stdout = $out.GetAwaiter().GetResult()
        $stderr = $err.GetAwaiter().GetResult()
        Put (Join-Path $Evidence "$name.stdout.txt") $stdout
        Put (Join-Path $Evidence "$name.stderr.txt") $stderr
        $phases.Add(@{ phase = $name; seconds = $watch.Elapsed.TotalSeconds; exitCode = $p.ExitCode })
        Check ($p.ExitCode -eq $expected) "$name expected exit $expected; got $($p.ExitCode): $stderr $stdout"
        return $(if ($expected -eq 0) { $stdout } else { $stderr })
    } finally { $p.Dispose() }
}
function Api([string]$method, [string]$route, $body = $null, [string]$token = '') {
    # Reuse connections while observing state; one new client per probe exhausts Windows ephemeral ports.
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), "$url/api/v1/$route")
    try {
        if ($token) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token) }
        if ($null -ne $body) { $request.Content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 12 -Compress), [Text.Encoding]::UTF8, 'application/json') }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            Check $response.IsSuccessStatusCode "$method $route failed: $($response.StatusCode) $text"
            if ($text) { return ($text | ConvertFrom-Json) }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Until([scriptblock]$condition, [string]$message, [int]$seconds = 60) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($hub.HasExited) { throw 'Scratch hub exited.' }
        if (& $condition) { $phases.Add(@{ phase = $message; seconds = $watch.Elapsed.TotalSeconds }); return }
        # The observed condition determines completion; real time only bounds a hung test.
    } while ($watch.Elapsed.TotalSeconds -lt $seconds)
    throw "Timed out: $message"
}
try {
    $names = @('PATH','MUTHUR_HOME','MUTHUR_URL','MUTHUR_AGENT','MUTHUR_TOKEN','MUTHUR_THROUGHPUT_FIXTURE','MUTHUR_THROUGHPUT_FAIL',
        'Muthur__BackgroundServices','Muthur__ConductorIntervalSeconds') +
        @(Get-ChildItem Env: | Where-Object Name -Match '^MUTHUR_|^Muthur__' | Select-Object -ExpandProperty Name)
    foreach ($name in $names | Select-Object -Unique) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); if ($name -ne 'PATH') { [Environment]::SetEnvironmentVariable($name, $null) } }
    $env:MUTHUR_HOME = Join-Path $scratch 'home'
    $env:MUTHUR_URL = $url
    $env:Muthur__BackgroundServices = 'true'
    $env:Muthur__ConductorIntervalSeconds = '15'
    $env:MUTHUR_THROUGHPUT_FIXTURE = Join-Path $scratch 'fixture.json'
    Put (Join-Path $repo 'specs/T-1.md') '# T-1 frozen worker fixture'
    Put (Join-Path $repo 'specs/T-3.md') '# T-3 frozen resumption fixture'
    Put (Join-Path $repo 'muthur.project.json') '{"key":"fixture","build":"echo fixture build","test":"echo fixture test"}'
    $null = Run 'git' @('init','-q','-b','main') 'git-init'
    $null = Run 'git' @('config','user.name','T95 Fixture') 'git-name'
    $null = Run 'git' @('config','user.email','t95@example.invalid') 'git-email'
    $null = Run 'git' @('add','.') 'git-add'
    $null = Run 'git' @('-c','commit.gpgsign=false','commit','-qm','Fixture') 'git-commit'
    $bin = Join-Path $scratch 'bin'
    $node = (Get-Command node).Source
    $fixtureScript = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures/throughput-harness.mjs')).Path
    Put (Join-Path $bin 'codex.cmd') ('@"' + $node + '" "' + $fixtureScript + '" %*')
    $env:PATH = "$bin;$env:PATH"
    Put (Join-Path $env:MUTHUR_HOME 'harnesses.json') '{"tiers":{"implementer":[{"harness":"codex","model":"fixture","account":"test"}],"mastermind":[{"harness":"codex","model":"fixture","account":"test"}]}}'
    $hub = Child $server @() $scratch
    $hubOut = $hub.StandardOutput.ReadToEndAsync()
    $hubErr = $hub.StandardError.ReadToEndAsync()
    Until { try { $null = Api GET 'status'; $true } catch { $false } } 'hub-ready'
    $founder = [IO.File]::ReadAllText((Join-Path $env:MUTHUR_HOME 'founder.token')).Trim()
    $registered = Api POST 'agents/register' @{ name = 'fixture-owner'; harness = 'test'; model = 'fixture' }
    $token = $registered.token
    $null = Api POST 'projects' @{ key = 'fixture'; repoPath = $repo; defaultBranch = 'main'; requiredValidators = @(); ingestSources = @() } $founder
    $workerTask = Api POST 'tasks' @{ project = 'fixture'; title = 'Worker fixture' } $token
    $null = Api POST "tasks/$($workerTask.id)/claim" @{} $token
    $prerequisite = Api POST 'tasks' @{ project = 'fixture'; title = 'Protected prerequisite' } $token
    $null = Api POST "tasks/$($prerequisite.id)/claim" @{} $token
    $null = Api POST 'requests' @{ task = $prerequisite.id; question = 'Human prerequisite remains protected'; kind = 'human' } $token
    $resume = Api POST 'tasks' @{ project = 'fixture'; title = 'Resume fixture' } $token
    Put $env:MUTHUR_THROUGHPUT_FIXTURE (@{ url = $url; evidence = $Evidence; prerequisite = $prerequisite.id; resumeTask = $resume.id } | ConvertTo-Json)
    $env:MUTHUR_AGENT = 'fixture-owner'
    $env:MUTHUR_TOKEN = $token
    $worked = (Run $CliPath @('worker','run','--task',$workerTask.id,'--spec','specs/T-1.md','--base','HEAD') 'worker-success') | ConvertFrom-Json
    Check ($worked.success -and $worked.committedByLauncher) 'Worker result was not committed by launcher.'
    Check ($worked.baseBranch -eq 'main' -and $worked.defaultBranch -eq 'main' -and $worked.baseCommit.Length -eq 40 -and $worked.specBlob.Length -eq 40) 'Missing dispatch provenance.'
    $env:MUTHUR_THROUGHPUT_FAIL = '1'
    $failed = (Run $CliPath @('worker','run','--task',$workerTask.id,'--spec','specs/T-1.md') 'worker-failed' 1) | ConvertFrom-Json
    Check (-not $failed.success -and $failed.failureKind -eq 'process_exit' -and $failed.report.Contains('T95 intentional process failure')) 'Process failure was hidden.'
    $env:MUTHUR_THROUGHPUT_FAIL = $null
    $env:MUTHUR_AGENT = $null
    $env:MUTHUR_TOKEN = $null
    $null = Api POST 'conductor/orchestrators' @{ enabled = $true } $founder
    $null = Api POST 'conductor' @{ enabled = $true } $founder
    Until { Test-Path (Join-Path $Evidence 'requests.json') } 'child-asked'
    Until { @((Api GET "tasks/$($resume.id)").events | Where-Object type -EQ 'conductor.orchestrator_exited').Count -gt 0 } 'blocked-child-exited'
    $requests = Get-Content (Join-Path $Evidence 'requests.json') -Raw | ConvertFrom-Json
    $null = Api POST "requests/$($requests[0])/answer" @{ answer = 'T95 approved scope' } $founder
    Check ((Api GET "tasks/$($resume.id)").task.state -eq 'blocked') 'First answer incorrectly unblocked multiple questions.'
    $answeredAt = [DateTimeOffset]::UtcNow
    $null = Api POST "requests/$($requests[1])/answer" @{ answer = 'T95 preserve prerequisite' } $founder
    Until { Test-Path (Join-Path $Evidence 'resumed.json') } 'answered-owner-resumed' 45
    $delay = ([DateTimeOffset]::UtcNow - $answeredAt).TotalSeconds
    Check ($delay -lt 45) 'Recovery waited for the 30-minute lease.'
    Until { (Api GET 'conductor').running -eq 0 } 'resumed-child-exited'
    $detail = Api GET "tasks/$($resume.id)"
    Check ($detail.task.state -eq 'backlog' -and $detail.task.dependsOn -contains $prerequisite.id) 'Resumption lost the prerequisite.'
    Check (@($detail.events | Where-Object type -EQ 'conductor.no_verdict').Count -eq 0) 'Dependency wait counted as an unproductive session.'
    $receipts = Api GET 'receipts?hours=1'
    Check (@($receipts.runs | Where-Object runId -EQ $worked.runId).Count -eq 1) 'Success receipt missing.'
    Check (@($receipts.runs | Where-Object { $_.runId -eq $failed.runId -and $_.exitCode -eq 7 }).Count -eq 1) 'Failure receipt missing.'
    Put (Join-Path $Evidence 'receipts.json') ($receipts | ConvertTo-Json -Depth 30)
    Put (Join-Path $Evidence 'task.json') ($detail | ConvertTo-Json -Depth 30)
    $events = @(Api GET 'events?since=0&limit=1000')
    $eventsFile = Join-Path $Evidence 'events.json'
    Put $eventsFile ($events | ConvertTo-Json -Depth 30)
    $since = ([DateTimeOffset]$events[0].at).ToString('o')
    $until = [DateTimeOffset]::UtcNow.ToString('o')
    $measurement = (Run $node @((Join-Path $PSScriptRoot 'measure-throughput.mjs'), '--events', $eventsFile,
        '--since', $since, '--until', $until, '--output', (Join-Path $Evidence 'measurement.json')) 'measure-recovery') | ConvertFrom-Json
    $sample = $measurement.conductor.answeredExitedOwnerToNextClaimMinutes
    $unblocked = @($detail.events | Where-Object type -EQ 'task.unblocked')[-1]
    $claimed = @($detail.events | Where-Object { $_.type -eq 'task.claimed' -and $_.seq -gt $unblocked.seq })[0]
    $expectedMinutes = (([DateTimeOffset]$claimed.at) - ([DateTimeOffset]$unblocked.at)).TotalMinutes
    Check ($sample.n -eq 1 -and [Math]::Abs($sample.mean - $expectedMinutes) -lt 0.001) 'Measurement lost or mis-timed the actual recovery sample.'
    Check ($measurement.conductor.answeredExitedOwnersStillWaiting -eq 0) 'Completed recovery incorrectly remains pending.'
    Put (Join-Path $Evidence 'result.json') (@{ success = $true; answerToResumedSeconds = $delay; recoveryMetric = $sample; url = $url; phases = $phases } | ConvertTo-Json -Depth 8)
    Write-Output "PASS: installed worker provenance, identity scrubbing, Git trust, failure reporting, receipts, latest decisions and recovery in $([Math]::Round($delay, 2)) seconds. Evidence: $Evidence"
} finally {
    $http.Dispose()
    if ($null -ne $hub) {
        if (-not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit() }
        Put (Join-Path $Evidence 'hub.stdout.txt') $hubOut.GetAwaiter().GetResult()
        Put (Join-Path $Evidence 'hub.stderr.txt') $hubErr.GetAwaiter().GetResult()
        $hub.Dispose()
    }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    $relative = [IO.Path]::GetRelativePath($tempRoot, [IO.Path]::GetFullPath($scratch))
    if ($relative -notmatch '^t95-[0-9a-f]{32}$') { throw "Refusing cleanup outside scratch root: $scratch" }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
