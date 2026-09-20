# Run against an installed AOT CLI; no hub is needed.
param([Parameter(Mandatory)][string]$CliPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This verification requires Windows FileShare.None behavior.' }
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = [IO.Path]::GetFullPath((Join-Path $tempRoot ('t82-' + [Guid]::NewGuid().ToString('N'))))
$savedEnv = @{}
foreach ($name in 'MUTHUR_KIT', 'MUTHUR_HOME', 'MUTHUR_URL') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Put([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function Snapshot([string]$Repo) {
    return (@(Get-ChildItem -LiteralPath $Repo -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Repo, $_.FullName)
        if ($_.PSIsContainer) { "D $relative" }
        else { "F $relative $([Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)))" }
    }) -join "`n")
}
function RunCli([string]$Repo) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $CliPath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $scratch
    foreach ($argument in 'kit', 'install', '--harness', 'scratch', '--repo', $Repo) {
        $psi.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    try {
        Check ($process.Start()) 'CLI did not start.'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'CLI timed out.'
        }
        return [pscustomobject]@{
            Exit = $process.ExitCode
            Stdout = $stdout.GetAwaiter().GetResult()
            Stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

try {
    $env:MUTHUR_KIT = Join-Path $scratch 'kit'
    $env:MUTHUR_HOME = Join-Path $scratch 'home'
    $env:MUTHUR_URL = 'http://127.0.0.1:1'
    [IO.Directory]::CreateDirectory($env:MUTHUR_HOME) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $env:MUTHUR_KIT 'core')) | Out-Null
    $harnessDir = Join-Path $env:MUTHUR_KIT 'scratch'
    $manifest = Join-Path $harnessDir 'kit.json'
    $entries = @('a', 'b', 'c' | ForEach-Object { @{ from = "$_.md"; to = "docs/$_.md" } })
    foreach ($name in 'a', 'b', 'c') { Put (Join-Path $harnessDir "$name.md") "procedure $name" }
    Put $manifest (@{ files = $entries } | ConvertTo-Json -Depth 4)

    foreach ($case in 'source', 'manifest') {
        $repo = Join-Path $scratch "repo-$case"
        Put (Join-Path $repo 'sentinel') "sentinel`r`n"
        Put (Join-Path $repo 'docs/a.md') "original a`r`n"
        Put (Join-Path $repo '.gitignore') "local/`r`n"
        Put (Join-Path $repo 'muthur.project.json') '{"key":"existing"}'
        [IO.Directory]::CreateDirectory((Join-Path $repo 'empty')) | Out-Null
        $before = Snapshot $repo
        $lockedPath = if ($case -eq 'source') { Join-Path $harnessDir 'c.md' } else { $manifest }
        $prefix = if ($case -eq 'source') {
            "$lockedPath could not be read while validating ${manifest}: IOException: "
        } else { "${manifest} could not be read: IOException: " }
        $handle = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $result = RunCli $repo
            Check ($result.Exit -eq 2) "$case refusal: expected exit 2, got $($result.Exit): $($result.Stderr)"
            Check ([string]::IsNullOrWhiteSpace($result.Stdout)) "$case refusal printed stdout success."
            $errorBody = $result.Stderr | ConvertFrom-Json
            Check ($errorBody.code -ceq 'invalid_manifest') "$case refusal: wrong error code."
            Check ($errorBody.message.StartsWith($prefix, [StringComparison]::Ordinal)) "$case refusal: wrong path/type prefix: $($errorBody.message)"
            Check ($errorBody.message.EndsWith('The repository was not changed. It is safe to re-run the command after resolving the read failure.', [StringComparison]::Ordinal)) "$case refusal: missing unchanged/retry assurance."
            Check ($errorBody.message -notmatch 'defect|report') "$case refusal blamed a defect or requested a report."
            Check ((Snapshot $repo) -ceq $before) "$case refusal changed repository bytes or directory inventory."
        }
        finally { $handle.Dispose() }

        $retry = RunCli $repo
        Check ($retry.Exit -eq 0) "$case retry failed: exit $($retry.Exit): $($retry.Stderr)"
        foreach ($name in 'a', 'b', 'c') {
            Check ([IO.File]::ReadAllText((Join-Path $repo "docs/$name.md")) -ceq "procedure $name") "$case retry: wrong installed content for $name."
        }
        Write-Output "$case refusal, unchanged repository, and retry: PASS"
    }
}
finally {
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $relative = [IO.Path]::GetRelativePath($tempRoot, $resolvedScratch)
    if ($relative -notmatch '^t82-[0-9a-f]{32}$' -or [IO.Path]::IsPathRooted($relative)) {
        throw "Refusing cleanup outside the unique scratch directory: $resolvedScratch"
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
