param([string]$OutputDirectory = 'artifacts/xml-documentation-policy')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
[IO.Directory]::CreateDirectory($output) | Out-Null
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('xml-policy-' + [guid]::NewGuid().ToString('N'))
$inventory = [ordered]@{ sourceHead = ''; sdk = ''; dateUtc = [DateTimeOffset]::UtcNow.ToString('o'); runs = @(); fixtures = @(); failure = $null }

function Run([string]$Binary, [string[]]$Arguments, [string]$Label, [int]$TimeoutSeconds, [string]$WorkingDirectory = $root) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Binary)
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    try {
        if (-not $process.Start()) { throw "Could not start $Binary" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw "Could not stop $Label" }
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 10000)) {
            throw "Output capture timed out: $Label"
        }
        $out = $stdout.Result
        $err = $stderr.Result
        [IO.File]::WriteAllText((Join-Path $output "$Label.stdout.log"), $out)
        [IO.File]::WriteAllText((Join-Path $output "$Label.stderr.log"), $err)
        if ($timedOut) { throw "$Label exceeded $TimeoutSeconds seconds" }
        return [pscustomobject]@{
            command = @($Binary) + $Arguments; workingDirectory = $WorkingDirectory
            exit = $process.ExitCode; text = "$out`n$err"
            logs = @("$Label.stdout.log", "$Label.stderr.log")
        }
    }
    finally { $process.Dispose() }
}

function Diagnostics([string]$Text, [string]$RelativeTo) {
    $seen = @{}
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^\s*(?<file>.+?)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\):\s+(?:warning|error)\s+(?<code>CS\d+):\s+(?<message>.*?)\s+\[(?<project>.+?)\]\s*$') {
            $item = [ordered]@{
                project = [IO.Path]::GetRelativePath($RelativeTo, $Matches.project).Replace('\', '/')
                file = [IO.Path]::GetRelativePath($RelativeTo, $Matches.file).Replace('\', '/')
                line = [int]$Matches.line; column = [int]$Matches.column
                code = $Matches.code; message = $Matches.message
            }
            $key = $item | ConvertTo-Json -Compress
            $seen[$key] = [pscustomobject]$item
        }
    }
    return @($seen.Values | Sort-Object project, file, line, column, code, message)
}

function BuildResult($Run, [string]$Name, [string]$RelativeTo) {
    $diagnostics = @(Diagnostics $Run.text $RelativeTo)
    # Non-compiler failures are infrastructure failures, even alongside compiler errors.
    if ($Run.text -match '(?im)\b(?:error|warning)\s+(?:(?!CS\d+\b)[A-Z]+\d+\s*)?:' -or
        ($Run.exit -ne 0 -and $diagnostics.Count -eq 0)) {
        throw "Unexpected build failure/diagnostic in $Name (exit $($Run.exit)); inspect $($Run.logs -join ', ')"
    }
    return [pscustomobject]@{
        name = $Name; command = $Run.command; exit = $Run.exit; logs = $Run.logs
        diagnostics = $diagnostics; counts = @()
    }
}

try {
    $git = Run 'git' @('-c', ('safe.directory=' + $root.Replace('\', '/')), 'rev-parse', '--verify', 'HEAD^{commit}') 'head' 30
    if ($git.exit -ne 0) { throw 'Could not resolve source HEAD' }
    $inventory.sourceHead = $git.text.Trim()
    $sdk = Run 'dotnet' @('--version') 'sdk' 30
    if ($sdk.exit -ne 0) { throw 'Could not resolve SDK' }
    $inventory.sdk = $sdk.text.Trim()
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $restoreConfig = Join-Path $scratch 'NuGet.Config'
    [IO.File]::WriteAllText($restoreConfig, '<configuration><packageSources><clear /></packageSources></configuration>')
    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'Muthur.slnx') -Raw
    $projects = @($solution.SelectNodes('//Project') | ForEach-Object { $_.Path })
    $modes = [ordered]@{
        baseline = @()
        documentation = @('-p:GenerateDocumentationFile=true', '-p:TreatWarningsAsErrors=false')
        'documentation-no1591' = @('-p:GenerateDocumentationFile=true', '-p:NoWarn=1591')
    }
    foreach ($mode in $modes.Keys) {
        $run = Run 'dotnet' (@('build', 'Muthur.slnx', '-t:Rebuild', '--disable-build-servers', '-m:1', '-nologo', "-p:RestoreConfigFile=$restoreConfig", '-p:NuGetAudit=false') + $modes[$mode]) $mode 600
        $result = BuildResult $run $mode $root
        $inventory.runs += $result
        if ($mode -ne 'documentation-no1591' -and $run.exit -ne 0) { throw "$mode build failed" }
    }
    $codes = @(@('CS1570', 'CS1587', 'CS1591') + @($inventory.runs | ForEach-Object { $_.diagnostics } | ForEach-Object { $_.code }) | Sort-Object -Unique)
    foreach ($result in $inventory.runs) {
        $result.counts = @(foreach ($project in $projects) {
            foreach ($code in $codes) {
                [pscustomobject]@{ project = $project; code = $code; count = @($result.diagnostics | Where-Object { $_.project -eq $project -and $_.code -eq $code }).Count }
            }
        })
    }
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'global.json') -Destination $scratch
    [IO.File]::WriteAllText((Join-Path $scratch 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
    [IO.File]::WriteAllText((Join-Path $scratch 'Probe.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NoWarn>1591</NoWarn><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup></Project>')
    $fixtures = [ordered]@{
        valid = "/// <summary>A probe declaration.</summary>`npublic class Probe { }"
        duplicate = "/// <summary>First.</summary>`n/// <summary>Second.</summary>`npublic class Probe { }"
        stranded = "public class Probe {`n/// <summary>No following declaration.</summary>`n}"
        semantic = "/// <summary>Deletes all database records.</summary>`npublic class Probe { }"
    }
    foreach ($fixture in $fixtures.Keys) {
        [IO.File]::WriteAllText((Join-Path $scratch 'Probe.cs'), $fixtures[$fixture])
        foreach ($generation in 'false', 'true') {
            $name = "$fixture-$generation"
            $run = Run 'dotnet' @('build', 'Probe.csproj', '-t:Rebuild', '--disable-build-servers', '-m:1', '-nologo', "-p:GenerateDocumentationFile=$generation") $name 120 $scratch
            $result = BuildResult $run $name $scratch
            $expectedIds = @(if ($fixture -eq 'stranded' -and $generation -eq 'true') { 'CS1587' })
            $ids = @($result.diagnostics | ForEach-Object { $_.code } | Sort-Object -Unique)
            $expectedExit = if ($expectedIds.Count) { 1 } else { 0 }
            $matchesExpected = $result.exit -eq $expectedExit -and ($ids -join ',') -eq ($expectedIds -join ',')
            $inventory.fixtures += [pscustomobject]@{
                name = $fixture; generateDocumentationFile = $generation -eq 'true'; source = $fixtures[$fixture]
                command = $run.command; exit = $result.exit; diagnosticIds = $ids; diagnostics = $result.diagnostics
                expectedExit = $expectedExit; expectedDiagnosticIds = $expectedIds; matchesExpected = $matchesExpected; logs = $run.logs
            }
            if (-not $matchesExpected) { throw "Fixture $name contradicts the proposed compiler-coverage assumptions" }
        }
    }
}
catch {
    $inventory.failure = $_.Exception.Message
}
finally {
    [IO.File]::WriteAllText((Join-Path $output 'inventory.json'), ($inventory | ConvertTo-Json -Depth 12))
    $relative = [IO.Path]::GetRelativePath($tempRoot, [IO.Path]::GetFullPath($scratch))
    if ($relative -notmatch '^xml-policy-[0-9a-f]{32}$') { throw "Refusing cleanup outside fixture directory: $scratch" }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
if ($inventory.failure) { Write-Error $inventory.failure; exit 1 }
if ($inventory.runs.Count -ne 3 -or $inventory.fixtures.Count -ne 8) { throw 'Incomplete inventory' }
Write-Output "Evidence written to $output/inventory.json"
