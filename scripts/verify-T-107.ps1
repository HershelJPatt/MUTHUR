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

    # Lightweight installed subprocess fixture: two recoverers race only on synthetic, owned empty scratch.
    $fixture = Join-Path $OutputRoot 'cleanup-fixture'
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $fixtureId = [guid]::NewGuid()
    $scratch = Join-Path $fixture ('scratch-' + $fixtureId.ToString('N'))
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $ownership = [ordered]@{
        version = 1; runId = $fixtureId; output = $fixture; repository = $Repository
        scratch = $scratch; checkout = (Join-Path $scratch 'checkout'); commit = $first.commit
        runner = @{ pid = [int]::MaxValue; startUtcTicks = 1; executable = $cli }
        url = 'http://127.0.0.1:1'; server = $null; serverLaunchStarted = $false; cleaned = $false
    }
    $ownership | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture 'ownership.json')
    $fixtureEvidence = $first | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $fixtureEvidence.runId = $fixtureId
    $fixtureEvidence.status = 'running'
    $fixtureEvidence.cleanupSucceeded = $false
    $fixtureEvidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $fixture 'evidence.json')
    1..2 | ForEach-Object {
        $info = [Diagnostics.ProcessStartInfo]::new($cli)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in @('verify', 'cleanup', '--output', $fixture)) { $info.ArgumentList.Add($argument) }
        $fixtureProcesses.Add([Diagnostics.Process]::Start($info))
    }
    $successes = 0
    foreach ($process in $fixtureProcesses) {
        if (-not $process.WaitForExit(30000)) { throw 'Installed cleanup fixture exceeded its hang-detector budget.' }
        if ($process.ExitCode -eq 0) { $successes++ }
    }
    if ($successes -eq 0) { throw 'Neither installed cleanup contender succeeded.' }
    $null = Invoke-Installed @('verify', 'cleanup', '--output', $fixture)
    $recovered = Get-Content -LiteralPath (Join-Path $fixture 'evidence.json') -Raw | ConvertFrom-Json
    if ([IO.Directory]::Exists($scratch) -or $recovered.status -ne 'interrupted') { throw 'Interrupted cleanup failed.' }
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
    foreach ($output in $outputs) {
        if ([IO.File]::Exists((Join-Path $output 'ownership.json'))) {
            & $cli verify cleanup --output $output | Set-Content -LiteralPath (Join-Path $output 'pilot-cleanup.json')
            if ($LASTEXITCODE -ne 0) { $defects.Add("Recovery cleanup failed for $output") }
        }
    }
    [ordered]@{
        cohort = 'T-107-preparation-v1'; startedUtc = $started; endedUtc = [datetimeoffset]::UtcNow
        sampleCount = $runs.Count; runs = @($runs.ToArray()); success = ($completed -and $defects.Count -eq 0)
        executionModelCalls = 0; defects = @($defects.ToArray())
        missingRealWorldData = @('Post-landing installed revision and first live-use measurement remain follow-ups; no controlled speedup claim.')
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputRoot 'pilot.json')
    $env:MUTHUR_HOME = $previousHome
    $env:MUTHUR_URL = $previousUrl
}
if ($defects.Count -gt 0) { throw 'Pilot cleanup failed; see pilot.json.' }
