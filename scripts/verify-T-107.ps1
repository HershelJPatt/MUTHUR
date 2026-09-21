param(
    [Parameter(Mandatory)][string]$InstallPath,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Ref,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$InstallPath = [IO.Path]::GetFullPath($InstallPath)
$Repository = [IO.Path]::GetFullPath($Repository)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$cli = Join-Path $InstallPath 'muthur.exe'
if (-not [IO.File]::Exists($cli)) { throw 'An installed muthur.exe is required.' }
if ([IO.Directory]::Exists($OutputRoot) -or [IO.File]::Exists($OutputRoot)) { throw 'OutputRoot must be new.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$cache = Join-Path $OutputRoot 'cache'
$previousHome = $env:MUTHUR_HOME
$previousUrl = $env:MUTHUR_URL
$env:MUTHUR_HOME = Join-Path $OutputRoot 'driver-home'
$env:MUTHUR_URL = 'http://127.0.0.1:1'
$started = [datetimeoffset]::UtcNow
$runs = [Collections.Generic.List[object]]::new()
$outputs = [Collections.Generic.List[string]]::new()
$defects = [Collections.Generic.List[string]]::new()
$fixtureProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$completed = $false
$fixtures = [Collections.Generic.List[object]]::new()
$fixtureOutcomes = [Collections.Generic.List[object]]::new()

function Invoke-Installed([string[]]$Arguments) {
    $text = & $cli @Arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Installed CLI exited ${code}: $($text -join [Environment]::NewLine)" }
    ($text -join [Environment]::NewLine) | ConvertFrom-Json
}

function Invoke-Run([string]$Name, [string]$ExpectedCache) {
    $output = Join-Path $OutputRoot $Name
    $outputs.Add($output)
    $result = Invoke-Installed @('verify', 'run', '--repo', $Repository, '--ref', $Ref, '--spec', 'specs/T-107.md',
        '--recipe', 'muthur', '--output', $output, '--cache', $cache, '--task', 'T-107')
    $evidence = Get-Content -LiteralPath $result.evidencePath -Raw | ConvertFrom-Json
    if ($result.status -ne 'success' -or $evidence.status -ne 'success' -or -not $evidence.cleanupSucceeded) {
        throw "$Name did not finish successfully with cleanup."
    }
    if ($evidence.cacheStatus -ne $ExpectedCache) { throw "$Name expected $ExpectedCache, got $($evidence.cacheStatus): $($evidence.cacheReason)" }
    if (-not $evidence.testsStarted -or -not $evidence.testsCompleted) { throw "$Name did not run fresh tests." }
    foreach ($stage in @('build', 'test', 'up', 'readiness', 'status', 'down')) {
        if (@($evidence.stages | Where-Object { $_.name -eq $stage -and $_.status -eq 'success' }).Count -ne 1) {
            throw "$Name has no unique successful $stage stage."
        }
    }
    $preparation = @($evidence.stages | Where-Object name -eq 'preparation')
    if ($ExpectedCache -eq 'hit' -and $preparation.Count -ne 0) { throw 'A hit unexpectedly prepared an installation.' }
    $runs.Add([pscustomobject]@{
        runId = $evidence.runId; revision = $evidence.commit; cache = $evidence.cacheStatus
        startedUtc = $evidence.startedUtc; endedUtc = $evidence.endedUtc
        setupMilliseconds = (@($evidence.stages | Where-Object { $_.name -eq 'checkout' -or $_.name -like 'probe-*' } |
            Measure-Object durationMilliseconds -Sum).Sum)
        preparationMilliseconds = (@($preparation | Measure-Object durationMilliseconds -Sum).Sum)
        evidencePath = $result.evidencePath
    })
    return $evidence
}

function Read-Barrier($Fixture, [string]$Phase) {
    $budget = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(20))
    try { $line = $Fixture.Reader.ReadLineAsync($budget.Token).AsTask().GetAwaiter().GetResult() }
    finally { $budget.Dispose() }
    if (-not $line.StartsWith($Phase + ':')) { throw "Expected fixture $Phase barrier, got '$line'." }
    return $line
}

function Start-Fixture([string]$Name) {
    $output = Join-Path $OutputRoot $Name
    $outputs.Add($output)
    $pipeName = 'muthur-pilot-' + [guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
    $info = [Diagnostics.ProcessStartInfo]::new($cli)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    foreach ($argument in @('verify', 'containment-fixture', '--output', $output, '--barrier', $pipeName)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $fixtureProcesses.Add($process)
    $fixture = [pscustomobject]@{ Process = $process; Pipe = $pipe; Reader = $null; Writer = $null; Output = $output; Child = $null }
    $fixtures.Add($fixture)
    $budget = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(20))
    try { $pipe.WaitForConnectionAsync($budget.Token).GetAwaiter().GetResult() }
    finally { $budget.Dispose() }
    $fixture.Reader = [IO.StreamReader]::new($pipe)
    $fixture.Writer = [IO.StreamWriter]::new($pipe); $fixture.Writer.AutoFlush = $true
    $null = Read-Barrier $fixture 'registered'
    $fixture.Writer.WriteLine('go')
    $null = Read-Barrier $fixture 'suspended'
    $fixture.Writer.WriteLine('go')
    $ready = Read-Barrier $fixture 'ready'
    $fixture.Child = [Diagnostics.Process]::GetProcessById([int]$ready.Split(':')[1])
    return $fixture
}

try {
    $first = Invoke-Run 'first' 'miss'
    $second = Invoke-Run 'second' 'hit'
    if ($first.key -ne $second.key -or $second.cacheProducer -ne $first.runId) { throw 'Hit producer/key mismatch.' }
    $artifact = [IO.Path]::GetFullPath((Join-Path $cache "$($first.key)/install/muthur.exe"))
    if (-not $artifact.StartsWith($cache + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Corruption fixture escaped the owned cache.'
    }
    [IO.File]::AppendAllText($artifact, 'T-107 corruption fixture')
    $null = Invoke-Run 'corrupted' 'rejected'

    # Each real child remains alive after its intermediate parent exits. The two run-owned jobs coexist.
    $firstFixture = Start-Fixture 'cancel-fixture'
    $secondFixture = Start-Fixture 'hard-kill-fixture'
    $firstFixture.Writer.WriteLine('cancel')
    if (-not $firstFixture.Process.WaitForExit(20000) -or $firstFixture.Process.ExitCode -ne 130) { throw 'Installed cancellation failed.' }
    if (-not $firstFixture.Child.WaitForExit(10000)) { throw 'Cancelled fixture orphaned its child.' }
    if ($secondFixture.Child.HasExited) { throw 'Cancelling one run terminated the concurrent run.' }
    $secondFixture.Process.Kill() # Only the supervisor: the job must contain the detached child.
    if (-not $secondFixture.Process.WaitForExit(10000) -or -not $secondFixture.Child.WaitForExit(10000)) { throw 'Hard interruption orphaned a child.' }
    foreach ($fixture in @($firstFixture, $secondFixture)) {
        $null = Invoke-Installed @('verify', 'cleanup', '--output', $fixture.Output)
        $null = Invoke-Installed @('verify', 'cleanup', '--output', $fixture.Output)
        $recovered = Get-Content -LiteralPath (Join-Path $fixture.Output 'evidence.json') -Raw | ConvertFrom-Json
        if (-not $recovered.cleanupSucceeded -or @(Get-ChildItem -LiteralPath $fixture.Output -Directory -Filter 'scratch-*').Count -ne 0) {
            throw 'Installed fixture cleanup failed.'
        }
        $fixtureOutcomes.Add([pscustomobject]@{ output = $fixture.Output; status = $recovered.status; childExited = $fixture.Child.HasExited })
    }
    $completed = $true
}
catch {
    $defects.Add($_.Exception.Message)
    throw
}
finally {
    foreach ($process in $fixtureProcesses) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($fixture in $fixtures) {
        if ($null -ne $fixture.Child) { $fixture.Child.Dispose() }
        if ($null -ne $fixture.Writer) { $fixture.Writer.Dispose() }
        if ($null -ne $fixture.Reader) { $fixture.Reader.Dispose() }
        $fixture.Pipe.Dispose()
    }
    foreach ($output in $outputs) {
        if ([IO.File]::Exists((Join-Path $output 'ownership.json'))) {
            & $cli verify cleanup --output $output | Set-Content -LiteralPath (Join-Path $output 'pilot-cleanup.json')
            if ($LASTEXITCODE -ne 0) { $defects.Add("Recovery cleanup failed for $output") }
        }
    }
    [ordered]@{
        cohort = 'T-107-preparation-v1'; startedUtc = $started; endedUtc = [datetimeoffset]::UtcNow
        sampleCount = $runs.Count; runs = @($runs.ToArray()); success = ($completed -and $defects.Count -eq 0)
        executionModelCalls = 0; defects = @($defects.ToArray()); containmentFixtures = @($fixtureOutcomes.ToArray())
        missingRealWorldData = @('Post-landing installed revision and first live-use measurement remain follow-ups; no controlled speedup claim.')
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputRoot 'pilot.json')
    $env:MUTHUR_HOME = $previousHome
    $env:MUTHUR_URL = $previousUrl
}
if ($defects.Count -gt 0) { throw 'Pilot cleanup failed; see pilot.json.' }

