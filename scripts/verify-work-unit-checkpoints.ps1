# Installed-only, deterministic recovery fixture. No model calls or live hub mutations.
param(
    [Parameter(Mandatory)][string]$CliPath,
    [string]$Evidence = (Join-Path $PSScriptRoot '../artifacts/t101-evidence'),
    [ValidateSet('', 'before', 'after')][string]$OwnerPhase = '',
    [string]$Repo = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$Evidence = [IO.Path]::GetFullPath($Evidence)
$processes = [Collections.Generic.List[object]]::new()
$http = [Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(10)
$sequence = 0
$token = ''
$graph = $null
function Put([string]$path, [string]$value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $value)
}
function Json($value) { ConvertTo-Json -InputObject $value -Depth 50 }
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Child([string]$binary, [string[]]$arguments, [string]$directory, [string]$kind) {
    $psi = [Diagnostics.ProcessStartInfo]::new($binary)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $directory
    foreach ($argument in $arguments) { $psi.ArgumentList.Add($argument) }
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    Check $p.Start() "Could not start $kind"
    $processes.Add(@{ kind = $kind; pid = $p.Id; at = [DateTimeOffset]::UtcNow.ToString('o') })
    return $p
}
function Run([string]$binary, [string[]]$arguments, [string]$name, [int]$expected = 0) {
    $p = Child $binary $arguments $Repo $name
    try {
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(120000)) { $p.Kill($true); $p.WaitForExit(); throw "$name timed out" }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        $script:sequence++
        $prefix = "$OwnerPhase-$sequence-$name"
        Put (Join-Path $Evidence "$prefix.stdout.txt") $out
        Put (Join-Path $Evidence "$prefix.stderr.txt") $err
        Check ($p.ExitCode -eq $expected) "$name exit $($p.ExitCode), expected ${expected}: $err $out"
        if ($expected -eq 0) { return $out.Trim() }
        return $err
    } finally {
        if (-not $p.HasExited) { $p.Kill($true); Check ($p.WaitForExit(10000)) "$name failed to stop" }
        $p.Dispose()
    }
}
function Git([string[]]$arguments) { Run 'git' $arguments 'git' }
function Api([string]$method, [string]$route, $body = $null, [string]$auth = $token, [int]$expected = 200) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), "$env:MUTHUR_URL/api/v1/$route")
    try {
        if ($auth) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $auth) }
        if ($null -ne $body) { $request.Content = [Net.Http.StringContent]::new((Json $body), [Text.Encoding]::UTF8, 'application/json') }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($expected -eq 200) { Check $response.IsSuccessStatusCode "$method $route failed: $text" }
            else { Check ([int]$response.StatusCode -eq $expected) "$method $route expected $expected, got $($response.StatusCode): $text" }
            if ($text) { return ($text | ConvertFrom-Json) }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function ReadGraph { $script:graph = Api GET 'tasks/T-1/units' }
function Unit([string]$id) { $graph.units | Where-Object id -EQ $id }
function Request([string]$id, [string]$action) {
    return @{ expectedRevision = $graph.revision; unitId = $id; attemptId = (Unit $id).attempt.attemptId; action = $action }
}
function Checkpoint($request, [int]$expected = 0) {
    $script:sequence++
    $file = Join-Path $Evidence "$OwnerPhase-$sequence-checkpoint.json"
    Put $file (Json $request)
    $result = Run $CliPath @('task','checkpoint','T-1','--file',$file) 'checkpoint' $expected
    if ($expected -eq 0) { $script:graph = $result | ConvertFrom-Json }
    return $result
}
function StartUnit([string]$id, [string]$branch, [string]$reason = '') {
    $base = Git @('rev-parse','integration')
    $null = Checkpoint @{ expectedRevision = $graph.revision; unitId = $id; attemptId = [guid]::NewGuid().ToString();
        action = 'start'; baseCommit = $base; outputBranch = $branch; reason = $reason }
    $null = Git @('checkout','-b',$branch,$base)
}
function OutputUnit([string]$id) {
    Put (Join-Path $Repo "$id.txt") "output $id"
    $null = Git @('add',"$id.txt")
    $null = Git @('-c','commit.gpgsign=false','commit','-qm',"$id output")
    return (Git @('rev-parse','HEAD'))
}
function VerifyUnit([string]$id) {
    $output = (Unit $id).attempt.outputCommit
    $check = Git @('cat-file','-e',"${output}:$id.txt")
    $diff = Git @('diff',(Unit $id).attempt.baseCommit,$output,'--',"$id.txt")
    Check ($diff.Contains("+output $id")) "Unexpected $id diff"
    Put (Join-Path $Repo "evidence/$id.txt") "cat-file exit=0 $check`nReviewed diff:`n$diff"
    $null = Git @('add',"evidence/$id.txt")
    $null = Git @('-c','commit.gpgsign=false','commit','-qm',"$id review and check evidence")
    $commit = Git @('rev-parse','HEAD')
    $request = Request $id 'verify'
    $request.checks = @(@{ name = 'output'; exitCode = 0; evidencePath = "evidence/$id.txt"; evidenceCommit = $commit })
    $request.reviewEvidencePath = "evidence/$id.txt"
    $request.reviewEvidenceCommit = $commit
    $null = Checkpoint $request
}
function MergeUnit([string]$id, [bool]$record = $true) {
    $null = Git @('checkout','integration')
    $null = Git @('merge','--ff-only',(Unit $id).attempt.outputCommit)
    if ($record) { $null = Checkpoint (Request $id 'integrate') }
}

if ($OwnerPhase) {
    # This process owns the task, then exits. The parent restarts the hub before launching its successor.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $phaseStartedAt = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        $registered = Api POST 'agents/register' @{ name = "owner-$OwnerPhase"; harness = 'test'; model = 'fixture' }
        $token = $registered.token
        $env:MUTHUR_TOKEN = $token
        $env:MUTHUR_AGENT = "owner-$OwnerPhase"
        $null = Api POST 'tasks/T-1/claim' @{}
        if ($OwnerPhase -eq 'before') {
            $null = Api POST 'tasks/T-1/spec' @{ path = 'specs/T-1.md'; branch = 'integration' }
            $definition = @{ expectedRevision = 0; specBlob = (Git @('rev-parse','integration:specs/T-1.md')); integrationBranch = 'integration'; units = @(
                @{ id = 'a'; dependencies = @(); requiredChecks = @('output') },
                @{ id = 'b'; dependencies = @(); requiredChecks = @('output') },
                @{ id = 'c'; dependencies = @('a','b'); requiredChecks = @('output') }) }
            $file = Join-Path $Evidence 'definition.json'
            Put $file (Json $definition)
            $graph = (Run $CliPath @('task','units','T-1','--define',$file) 'define') | ConvertFrom-Json
            StartUnit 'a' 'worker/a'
            $null = OutputUnit 'a' # Simulate commit-before-report.
            $null = Checkpoint (Request 'a' 'reconcile')
            Check ((Unit 'a').attempt.reportedState -eq 'recovered' -and (Unit 'a').attempt.reviewState -eq 'pending') 'Recovery auto-reviewed output.'
            VerifyUnit 'a'
            MergeUnit 'a'
            StartUnit 'b' 'worker/b'
            $request = Request 'b' 'report'
            $request.outputCommit = OutputUnit 'b'
            $null = Checkpoint $request
            VerifyUnit 'b'
            MergeUnit 'b' $false # Simulate merge-before-integration-report.
            Put (Join-Path $Evidence 'before-graph.json') (Json $graph)
            $null = Api POST 'tasks/T-1/release' @{ reason = 'controlled owner exit' }
        } else {
            $null = Run $CliPath @('task','resume','T-1') 'resume'
            $graph = (Run $CliPath @('task','units','T-1') 'units') | ConvertFrom-Json
            foreach ($id in @('a','b')) {
                $null = Checkpoint (Request $id 'reconcile')
                Check ((Unit $id).attempt.reviewState -eq 'accepted' -and $null -ne (Unit $id).attempt.integrationCommit) "$id did not survive handoff"
            }
            $usefulSeconds = $watch.Elapsed.TotalSeconds
            $usefulAt = [DateTimeOffset]::UtcNow.ToString('o')
            Put (Join-Path $Evidence 'reused-graph.json') (Json $graph)
            StartUnit 'c' 'worker/c'
            $request = Request 'c' 'report'
            $request.outputCommit = OutputUnit 'c'
            $null = Checkpoint $request
            VerifyUnit 'c'
            MergeUnit 'c'
            Put (Join-Path $Evidence 'after-graph.json') (Json $graph)
            Put (Join-Path $Evidence 'recovery-timing.json') (Json @{ resumptionToFirstUsefulActionSeconds = $usefulSeconds;
                measurementStart = 'replacement owner phase entry, before registration and claim';
                startedAt = $phaseStartedAt; firstUsefulActionAt = $usefulAt })
            # Runtime-only credential, removed with scratch; never included in evidence.
            Put (Join-Path $env:MUTHUR_HOME 'replacement.token') $token
        }
    } finally {
        Put (Join-Path $Evidence "$OwnerPhase-processes.json") (Json $processes)
        $http.Dispose()
    }
    return
}

Check (-not (Test-Path -LiteralPath $Evidence) -or @(Get-ChildItem -LiteralPath $Evidence -Force).Count -eq 0) 'Use a fresh evidence directory.'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('t101-' + [guid]::NewGuid().ToString('N'))
$Repo = Join-Path $scratch 'repo'
$server = Join-Path (Split-Path $CliPath) 'server/Muthur.Server.exe'
$saved = @{}
$hub = $null
$hubOut = $null
$hubErr = $null
$hubNumber = 0
function StopHub {
    if ($null -eq $script:hub) { return }
    try {
        if (-not $hub.HasExited) { $hub.Kill($true); Check ($hub.WaitForExit(10000)) 'Scratch hub failed to stop.' }
        Put (Join-Path $Evidence "hub-$hubNumber.stdout.txt") $hubOut.GetAwaiter().GetResult()
        Put (Join-Path $Evidence "hub-$hubNumber.stderr.txt") $hubErr.GetAwaiter().GetResult()
    } finally { $hub.Dispose(); $script:hub = $null }
}
function StartHub {
    $script:hubNumber++
    $script:hub = Child $server @() $scratch 'hub'
    $script:hubOut = $hub.StandardOutput.ReadToEndAsync()
    $script:hubErr = $hub.StandardError.ReadToEndAsync()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Check (-not $hub.HasExited) 'Scratch hub exited before readiness.'
        try { $null = Api GET 'status'; return } catch { }
    } while ($watch.Elapsed.TotalSeconds -lt 60)
    throw 'Scratch hub readiness timed out.'
}
try {
    $names = @('MUTHUR_HOME','MUTHUR_URL','MUTHUR_TOKEN','MUTHUR_AGENT','Muthur__BackgroundServices') +
        @(Get-ChildItem Env: | Where-Object Name -Match '^MUTHUR_|^Muthur__' | Select-Object -ExpandProperty Name)
    foreach ($name in $names | Select-Object -Unique) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $env:MUTHUR_URL = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
    $listener.Stop()
    $env:MUTHUR_HOME = Join-Path $scratch 'home'
    $env:Muthur__BackgroundServices = 'false'
    Put (Join-Path $Repo 'specs/T-1.md') '# T-1 deterministic checkpoint fixture'
    Put (Join-Path $Repo 'muthur.project.json') '{"key":"fixture","build":"echo fixture","test":"echo fixture"}'
    $null = Git @('init','-q','-b','main')
    $null = Git @('config','user.name','T101 Fixture')
    $null = Git @('config','user.email','t101@example.invalid')
    $null = Git @('add','.')
    $null = Git @('-c','commit.gpgsign=false','commit','-qm','initial')
    $null = Git @('checkout','-b','integration')
    StartHub
    $founder = [IO.File]::ReadAllText((Join-Path $env:MUTHUR_HOME 'founder.token')).Trim()
    $null = Api PUT 'roles' @{ key = 'reviewer'; isValidator = $true; brief = 'Independent task validation remains required.' } $founder
    $null = Api POST 'projects' @{ key = 'fixture'; repoPath = $Repo; defaultBranch = 'main'; requiredValidators = @('reviewer'); ingestSources = @() } $founder
    $null = Api POST 'tasks' @{ project = 'fixture'; title = 'Three-unit recovery' } $founder
    $pwsh = (Get-Command pwsh).Source
    $null = Run $pwsh @('-NoProfile','-File',$PSCommandPath,'-CliPath',$CliPath,'-Evidence',$Evidence,'-OwnerPhase','before','-Repo',$Repo) 'owner-before'
    StopHub
    StartHub
    $null = Run $pwsh @('-NoProfile','-File',$PSCommandPath,'-CliPath',$CliPath,'-Evidence',$Evidence,'-OwnerPhase','after','-Repo',$Repo) 'owner-after'
    $token = [IO.File]::ReadAllText((Join-Path $env:MUTHUR_HOME 'replacement.token'))
    $env:MUTHUR_TOKEN = $token
    ReadGraph
    $detail = Api GET 'tasks/T-1'
    $started = @($detail.events | Where-Object type -EQ 'task.unit_started')
    $verified = @($detail.events | Where-Object type -EQ 'task.unit_verified')
    Check ($started.Count -eq 3 -and $verified.Count -eq 3) 'Handoff reran or reverified work.'
    Check (@($started | Where-Object actor -EQ 'owner-after').Count -eq 1) 'Replacement did more than C.'
    Put (Join-Path $Evidence 'handoff-ledger.json') (Json $detail)

    # Concurrent CAS race: exactly one mutation and one durable accepted event.
    $race = Request 'c' 'invalidate'; $race.reason = 'race probe'
    $raceClients = @([Net.Http.HttpClient]::new(), [Net.Http.HttpClient]::new())
    $contents = @()
    try {
        $pending = foreach ($client in $raceClients) {
            $client.Timeout = [TimeSpan]::FromSeconds(30)
            $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
            $content = [Net.Http.StringContent]::new((Json $race), [Text.Encoding]::UTF8, 'application/json')
            $contents += $content
            $client.PostAsync("$env:MUTHUR_URL/api/v1/tasks/T-1/units/checkpoint", $content)
        }
        $results = foreach ($pendingResponse in $pending) {
            $response = $pendingResponse.GetAwaiter().GetResult()
            try { @{ status = [int]$response.StatusCode; body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } }
            finally { $response.Dispose() }
        }
        Check (@($results | Where-Object status -EQ 200).Count -eq 1) 'Race did not accept exactly one mutation.'
        Check (@($results | Where-Object { $_.status -eq 409 -and ($_.body | ConvertFrom-Json).code -eq 'checkpoint_conflict' }).Count -eq 1) 'Race did not conflict.'
        Put (Join-Path $Evidence 'race.json') (Json $results)
    } finally { foreach ($content in $contents) { $content.Dispose() }; foreach ($client in $raceClients) { $client.Dispose() } }
    ReadGraph
    Check ($graph.revision -eq $race.expectedRevision + 1) 'Race advanced revision twice.'
    Check (@((Api GET 'tasks/T-1').events | Where-Object type -EQ 'task.unit_invalidated').Count -eq 1) 'Race recorded extra events.'
    $late = Request 'c' 'report'
    StartUnit 'c' 'worker/c2' 'replace invalidated attempt'
    $late.expectedRevision = $graph.revision
    $errorResponse = Api POST 'tasks/T-1/units/checkpoint' $late $token 409
    Check ($errorResponse.code -eq 'stale_attempt') 'Late attempt was not rejected.'
    $null = Git @('checkout','integration')
    $independent = Json (Unit 'b')
    $invalidate = Request 'a' 'invalidate'; $invalidate.reason = 'dependency probe'
    $null = Checkpoint $invalidate
    Check ((Unit 'c').attempt.reviewState -eq 'rejected' -and (Json (Unit 'b')) -eq $independent) 'Dependency invalidation damaged independent output.'

    # Independent B retains pinned proof until a real artifact is lost.
    $b = Unit 'b'
    $object = Git @('rev-parse',"$($b.attempt.reviewEvidenceCommit):evidence/b.txt")
    $objectPath = Join-Path $Repo ".git/objects/$($object.Substring(0,2))/$($object.Substring(2))"
    $bytes = [IO.File]::ReadAllBytes($objectPath)
    [IO.File]::SetAttributes($objectPath, [IO.FileAttributes]::Normal)
    [IO.File]::Delete($objectPath)
    $null = Checkpoint (Request 'b' 'reconcile')
    Check ((Unit 'b').attempt.reviewState -eq 'rejected' -and (Unit 'b').invalidationReason.Contains('missing')) 'Lost evidence was accepted.'
    [IO.File]::WriteAllBytes($objectPath, $bytes)
    $null = Git @('branch','-D','worker/b')
    $null = Checkpoint (Request 'b' 'reconcile')
    Check ((Unit 'b').invalidationReason.Contains('missing')) 'Missing output branch was ignored.'
    $null = Git @('branch','worker/b',$b.attempt.reviewEvidenceCommit)
    $integration = Git @('rev-parse','integration')
    # Same spec but no dispatched base ancestry: orphan history, not merely a changed spec.
    $null = Git @('checkout','--orphan','divergent')
    $null = Git @('-c','commit.gpgsign=false','commit','-qm','unrelated root with same tree')
    $divergent = Git @('rev-parse','HEAD')
    $null = Git @('branch','-f','integration',$divergent)
    $null = Checkpoint (Request 'b' 'reconcile')
    Check ((Unit 'b').invalidationReason.Contains('base')) 'Divergent base was ignored.'
    $null = Git @('branch','-f','integration',$integration)
    $null = Git @('checkout','integration')
    Put (Join-Path $Repo 'specs/T-1.md') '# T-1 amended'
    $null = Git @('add','specs/T-1.md')
    $null = Git @('-c','commit.gpgsign=false','commit','-qm','stale spec probe')
    $null = Checkpoint (Request 'b' 'reconcile')
    Check ((Unit 'b').attempt.nextAction -eq 'redefine graph') 'Stale spec did not require redefinition.'
    $null = Git @('reset','--hard',$integration)
    $implemented = Api POST 'tasks/T-1/implemented' @{ branch = 'integration' }
    Check ($implemented.state -eq 'validating' -and @($implemented.validations | Where-Object verdict -EQ 'pending').Count -eq 1) 'Unit state bypassed independent validation.'
    $land = Api POST 'tasks/T-1/land' @{} $token 422
    Check ($land.code -eq 'not_validated') 'Task landed without independent validation.'
    Put (Join-Path $Evidence 'task.json') (Json (Api GET 'tasks/T-1'))
    Put (Join-Path $Evidence 'events.json') (Json (Api GET 'events?since=0&limit=1000'))
    $before = Get-Content (Join-Path $Evidence 'before-graph.json') -Raw | ConvertFrom-Json
    $reused = Get-Content (Join-Path $Evidence 'reused-graph.json') -Raw | ConvertFrom-Json
    $reuseCount = 0
    foreach ($id in @('a','b')) {
        $old = $before.units | Where-Object id -EQ $id
        $new = $reused.units | Where-Object id -EQ $id
        Check ($old.attempt.attemptId -eq $new.attempt.attemptId -and $old.attempt.outputCommit -eq $new.attempt.outputCommit) "$id was replaced"
        $reuseCount++
    }
    Check (@($processes | Where-Object kind -EQ 'hub').Count -eq 2) 'Expected exactly two hub starts.'
    Check (@($processes | Where-Object kind -Like 'owner-*').Count -eq 2) 'Expected exactly two owner starts.'
    Put (Join-Path $Evidence 'result.json') (Json @{ success = $true; sampleCount = 1; reused = $reuseCount;
        unnecessarilyRerun = $started.Count - 3; repeatedVerificationCount = $verified.Count - 3;
        hubProcessStarts = @($processes | Where-Object kind -EQ 'hub').Count;
        ownerProcessStarts = @($processes | Where-Object kind -Like 'owner-*').Count;
        recovery = (Get-Content (Join-Path $Evidence 'recovery-timing.json') -Raw | ConvertFrom-Json);
        installedCliSha256 = (Get-FileHash -LiteralPath $CliPath -Algorithm SHA256).Hash;
        livePilot = 'not measured; T-114 depends on T-101' })
    Write-Output "PASS: synthetic checkpoint recovery; evidence: $Evidence"
} finally {
    try {
        StopHub
        Put (Join-Path $Evidence 'processes.json') (Json $processes)
        # The stopped database is the source ledger, including every graph and transition proof.
        if (Test-Path -LiteralPath (Join-Path $scratch 'home/muthur.db')) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $scratch 'home') -Filter 'muthur.db*') {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Evidence $file.Name)
            }
        }
        if (Test-Path -LiteralPath $Repo) { Copy-Item -LiteralPath $Repo -Destination (Join-Path $Evidence 'repo') -Recurse -Force }
    } finally {
        $http.Dispose()
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        $relative = [IO.Path]::GetRelativePath($tempRoot, [IO.Path]::GetFullPath($scratch))
        if ($relative -notmatch '^t101-[0-9a-f]{32}$') { throw "Refusing cleanup outside scratch root: $scratch" }
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }
}
