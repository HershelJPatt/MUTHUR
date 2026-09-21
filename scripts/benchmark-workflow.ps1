param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Output,
    [string]$Scenario,
    [ValidateRange(1, 10)][int]$Trials = 1,
    [ValidateSet('deterministic', 'real')][string]$Mode = 'deterministic',
    [ValidateSet('none', 'after-scratch', 'during-child')][string]$Fault = 'none'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$driver = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputRoot) { throw 'Output must be a new directory.' }
$runId = [guid]::NewGuid().ToString('n')
$scratch = Join-Path $outputRoot "scratch-$runId"
$children = [Collections.Generic.List[object]]::new()
$state = [ordered]@{ schemaVersion = 1; runId = $runId; mode = $Mode; status = 'incomplete'; agentBehavior = 'unmeasured'; paidSessions = 0; modelCalls = 0; baseline = $null; candidate = $null; driverRevision = $null; error = $null; scratch = $scratch; children = @(); cleanupComplete = $false }
$exitCode = 1

function Write-Json($Path, $Value) {
    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-Owned([string]$Exe, [string[]]$Arguments, [string]$Working, [string]$Log, [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 600, [switch]$Interrupt) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.WorkingDirectory = $Working
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -match '^(MUTHUR_|OPENAI_|ANTHROPIC_|CLAUDE_|CODEX_|GH_TOKEN$|GITHUB_TOKEN$|AZURE_OPENAI_|GOOGLE_API_KEY$)') { $info.Environment.Remove($key) | Out-Null }
    }
    $info.Environment['MUTHUR_HOME'] = Join-Path $scratch 'home'
    $info.Environment['MUTHUR_URL'] = 'http://127.0.0.1:1'
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.Environment['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER'] = '1'
    $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    $info.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $info.Environment['GIT_CONFIG_GLOBAL'] = Join-Path $scratch 'empty-gitconfig'
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $entry = $null
    try {
        if (-not $process.Start()) { throw "Cannot start $Exe" }
        $entry = [ordered]@{ pid = $process.Id; executable = $Exe; exited = $false; killed = $false }
        $children.Add($entry)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($Interrupt) { throw 'Injected cancellation during owned child execution.' }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "Child timeout: $Exe" }
        if ($process.ExitCode -ne 0) { throw "$Exe exited $($process.ExitCode); see $Log" }
        return $stdout.GetAwaiter().GetResult().Trim()
    } finally {
        if ($null -ne $entry) {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $entry.killed = $true
                if (-not $process.WaitForExit(30000)) { throw "Owned child $($entry.pid) did not stop." }
            }
            $entry.exited = $process.HasExited
            [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        }
        $process.Dispose()
    }
}

function Resolve-Revision([string]$Revision, [string]$Name) {
    if ($Revision.StartsWith('-')) { throw "Invalid $Name revision." }
    $sha = Invoke-Owned git @('rev-parse', '--verify', '--end-of-options', "$Revision^{commit}") $driver (Join-Path $outputRoot "$Name-resolve.log")
    if ($sha -notmatch '^[0-9a-f]{40}$') { throw "Invalid resolved $Name identity." }
    return $sha
}

try {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
    Write-Json (Join-Path $outputRoot 'comparison.json') $state
    $state.baseline = Resolve-Revision $Baseline 'baseline'
    $state.candidate = Resolve-Revision $Candidate 'candidate'
    $state.driverRevision = Resolve-Revision 'HEAD' 'driver'
    $manifestPath = Join-Path $driver 'benchmarks/workflow/scenarios.v1.json'
    $suite = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $ids = @($suite.scenarios | Where-Object { -not $Scenario -or $_.id -eq $Scenario } | ForEach-Object id)
    if ($ids.Count -eq 0) { throw "Unknown scenario: $Scenario" }
    $state['suiteVersion'] = $suite.version
    $state['suiteHash'] = $hash
    $state['scenarioIds'] = $ids
    $state['trials'] = $Trials
    $state['cohort'] = 'historical'
    $state['driverContentHashes'] = @(@(Get-ChildItem (Join-Path $driver 'tests/Muthur.Server.Tests/Benchmark') -File -Filter '*.cs') + @(Get-Item $manifestPath) | ForEach-Object { @{ file = [IO.Path]::GetRelativePath($driver, $_.FullName); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $driverBytes = [Text.Encoding]::UTF8.GetBytes(($state.driverContentHashes | ConvertTo-Json -Depth 5 -Compress))
    $state['driverContentHash'] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($driverBytes)).ToLowerInvariant()
    if ($Mode -eq 'real') {
        $state.status = 'unmeasured'
        $state.error = 'No approved admitted real-harness route in this slice; budget zero.'
    } else {
        New-Item -ItemType Directory -Path $scratch | Out-Null
        Set-Content -LiteralPath (Join-Path $scratch 'owner') -Value $runId
        $snapshot = Join-Path $scratch 'driver'
        foreach ($file in $state.driverContentHashes) {
            $target = Join-Path $snapshot $file.file
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $driver $file.file) -Destination $target
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'Driver changed during snapshot.' }
        }
        if ($Fault -eq 'after-scratch') { throw 'Injected failure after scratch creation.' }
        if ($Fault -eq 'during-child') {
            Invoke-Owned pwsh @('-NoProfile', '-Command', '[Threading.ManualResetEvent]::new($false).WaitOne()') $scratch (Join-Path $outputRoot 'cancelled-child.log') -Interrupt | Out-Null
        }
        $aggregates = @{}
        foreach ($label in @('baseline', 'candidate')) {
            $revision = $state[$label]
            $checkout = Join-Path $scratch $label
            $results = Join-Path $outputRoot $label
            New-Item -ItemType Directory -Path $checkout, $results | Out-Null
            $archive = Join-Path $scratch "$label.zip"
            Invoke-Owned git @('archive', '--format=zip', "--output=$archive", $revision) $driver (Join-Path $results 'archive.log') | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $checkout
            $testDirectory = Join-Path $checkout 'tests/Muthur.Server.Tests/Benchmark'
            # Remove only archived benchmark files in this invocation's verified scratch checkout.
            if (Test-Path $testDirectory) {
                $resolved = [IO.Path]::GetFullPath($testDirectory)
                if (-not $resolved.StartsWith($scratch + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe overlay path.' }
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
            New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
            Copy-Item -Path (Join-Path $snapshot 'tests/Muthur.Server.Tests/Benchmark/*.cs') -Destination $testDirectory
            $manifestTarget = Join-Path $checkout 'benchmarks/workflow/scenarios.v1.json'
            New-Item -ItemType Directory -Path (Split-Path $manifestTarget) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $snapshot 'benchmarks/workflow/scenarios.v1.json') -Destination $manifestTarget
            $projectPath = Join-Path $checkout 'tests/Muthur.Server.Tests/Muthur.Server.Tests.csproj'
            [xml]$project = Get-Content -LiteralPath $projectPath -Raw
            $content = @($project.SelectNodes('//Content') | Where-Object { $_.GetAttribute('Include') -match 'scenarios\.v1\.json$' })
            foreach ($node in $content) { $node.ParentNode.RemoveChild($node) | Out-Null }
            $group = $project.CreateElement('ItemGroup')
            $node = $project.CreateElement('Content')
            $node.SetAttribute('Include', '../../benchmarks/workflow/scenarios.v1.json')
            $node.SetAttribute('Link', 'Benchmark/scenarios.v1.json')
            $node.SetAttribute('CopyToOutputDirectory', 'PreserveNewest')
            $group.AppendChild($node) | Out-Null
            $project.Project.AppendChild($group) | Out-Null
            $project.Save($projectPath)
            $appData = Join-Path $scratch "$label-appdata"
            New-Item -ItemType Directory -Path (Join-Path $appData 'NuGet') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $appData 'NuGet/NuGet.Config') -Value '<configuration><packageSources><clear /></packageSources></configuration>'
            $environment = @{
                APPDATA = $appData; MUTHUR_BENCHMARK_OUTPUT = $results; MUTHUR_BENCHMARK_RUN = $runId
                MUTHUR_BENCHMARK_PRODUCT = $revision; MUTHUR_BENCHMARK_DRIVER = $state.driverRevision
                MUTHUR_BENCHMARK_DRIVER_HASH = $state.driverContentHash
                MUTHUR_BENCHMARK_TRIALS = "$Trials"; MUTHUR_BENCHMARK_SCENARIO = "$Scenario"
                TEMP = (Join-Path $scratch "$label-temp"); TMP = (Join-Path $scratch "$label-temp")
            }
            New-Item -ItemType Directory -Path $environment.TEMP | Out-Null
            Invoke-Owned dotnet @('build', $projectPath, '--disable-build-servers', '-p:NuGetAudit=false', '-p:RestoreSources=', '-p:UseSharedCompilation=false') $checkout (Join-Path $results 'build.log') $environment | Out-Null
            Invoke-Owned dotnet @('test', $projectPath, '--no-build', '--no-restore', '--filter', 'Category=WorkflowBenchmark', '--logger', 'trx;LogFileName=workflow.trx', '--results-directory', $results, '--disable-build-servers') $checkout (Join-Path $results 'test.log') $environment | Out-Null
            $aggregate = Get-Content -LiteralPath (Join-Path $results 'aggregate.json') -Raw | ConvertFrom-Json
            if ($aggregate.status -ne 'complete' -or $aggregate.sampleCount -ne ($ids.Count * $Trials) -or $aggregate.suiteHash -ne $hash -or $aggregate.suiteVersion -ne $suite.version -or $aggregate.cohort -ne 'historical' -or $aggregate.productRevision -ne $revision -or $aggregate.driverRevision -ne $state.driverRevision -or $aggregate.driverContentHash -ne $state.driverContentHash) { throw "Incomplete or incompatible $label report." }
            foreach ($id in $ids) {
                foreach ($index in 1..$Trials) {
                    $trialDirectory = Join-Path $results "$id/$index"
                    foreach ($file in @('trial.json', 'http.json', 'task-events.json', 'git.json')) {
                        if (-not (Test-Path -LiteralPath (Join-Path $trialDirectory $file) -PathType Leaf)) { throw "Missing $label evidence: $id/$index/$file" }
                    }
                    $trial = Get-Content -LiteralPath (Join-Path $trialDirectory 'trial.json') -Raw | ConvertFrom-Json
                    if ($trial.status -ne 'complete' -or -not $trial.grade.passed -or $trial.scenarioId -ne $id -or $trial.trialIndex -ne $index -or $trial.suiteHash -ne $hash -or $trial.cohort -ne 'historical' -or $trial.productRevision -ne $revision -or $trial.driverContentHash -ne $state.driverContentHash) { throw "Invalid $label trial: $id/$index" }
                    if ($id -ne 'no-verdict-loop' -and -not (Test-Path -LiteralPath (Join-Path $trialDirectory 'result.txt'))) { throw 'Missing graded file.' }
                }
            }
            $aggregates[$label] = $aggregate
        }
        $state['results'] = $aggregates
        $state.status = 'complete'
        $exitCode = 0
    }
} catch {
    $state.status = 'incomplete/error'
    $state.error = $_.Exception.Message
} finally {
    try {
        if (Test-Path -LiteralPath $scratch) {
            $resolved = [IO.Path]::GetFullPath($scratch)
            $ownerFile = Join-Path $resolved 'owner'
            if ($resolved -ne (Join-Path $outputRoot "scratch-$runId") -or -not $resolved.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Get-Content -LiteralPath $ownerFile -Raw).Trim() -ne $runId) { throw 'Scratch ownership check failed; retained for inspection.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        $state.cleanupComplete = $true
    } catch {
        $state.status = 'incomplete/error'
        $state.error = "$($state.error) Cleanup: $($_.Exception.Message)"
        $exitCode = 1
    }
    $state.children = @($children.ToArray())
    if (Test-Path -LiteralPath $outputRoot) {
        Write-Json (Join-Path $outputRoot 'comparison.json') $state
        @"
# Workflow revision comparison

Status: $($state.status)
Baseline: $($state.baseline)
Candidate: $($state.candidate)
Driver: $($state.driverRevision)
Mode: $Mode. Agent behavior: unmeasured. Paid sessions/model calls: 0.
Reason: $($state.error)

See comparison.json and each revision's report.md, aggregate.json, TRX, logs and raw trial evidence.
Compare only the identical suite hash, cohort, scenario selection and trial counts. No speedup claim.
"@ | Set-Content -LiteralPath (Join-Path $outputRoot 'comparison.md') -Encoding utf8
    }
}
exit $exitCode
