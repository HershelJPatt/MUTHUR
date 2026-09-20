param([Parameter(Mandatory)][string]$Cli)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Cli = [IO.Path]::GetFullPath($Cli)
$previousEnvironment = @{}
foreach ($name in @('MUTHUR_HOME','MUTHUR_URL','MUTHUR_KIT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$scratch = Join-Path $PSScriptRoot ('t57-verification-' + [guid]::NewGuid().ToString('n'))
$scratch = [IO.Path]::GetFullPath($scratch)
$allowed = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe scratch path' }
$links = [Collections.Generic.List[string]]::new()
$env:MUTHUR_HOME = Join-Path $scratch 'home'
$env:MUTHUR_URL = 'http://127.0.0.1:17557'
function Junction([string]$Link, [string]$Target) {
    New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null
    $links.Add($Link)
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function RunCase([string]$Name, [string]$To, [int]$Expected, [string]$Shape = 'out', [string]$From = 'good.md') {
    $case = Join-Path $scratch $Name
    $repo = Join-Path $case 'repo'
    $outside = Join-Path $case 'outside'
    $kit = Join-Path $case 'kit'
    $harness = Join-Path $kit 'probe'
    foreach ($dir in @($repo,$outside,$harness,(Join-Path $repo 'docs'))) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -LiteralPath (Join-Path $harness 'good.md') -Value 'T57 payload' -NoNewline
    Assert (Test-Path -LiteralPath (Join-Path $harness 'good.md')) 'FIXTURE source missing'
    Junction (Join-Path $repo 'link') $outside
    Junction (Join-Path $repo 'inward') (Join-Path $repo 'docs')
    Junction (Join-Path $repo 'chain') (Join-Path $repo 'link')
    $repoArg = $repo
    if ($Shape -eq 'repo-link') { $repoArg = Join-Path $case 'via'; Junction $repoArg $repo }
    if ($Shape -eq 'source') {
        Set-Content -LiteralPath (Join-Path $outside 'source.md') -Value 'outside source' -NoNewline
        Junction (Join-Path $harness 'source-link') $outside
    }
    if ($Shape -eq 'dangling') {
        $missing = Join-Path $case 'missing'
        New-Item -ItemType Directory -Path $missing | Out-Null
        Junction (Join-Path $repo 'dangling') $missing
        [IO.Directory]::Delete($missing)
    }
    $entries = @(@{from=$From;to=$To})
    if ($Shape -eq 'collision') { $entries += @{from='good.md';to='docs/x.md/y.md'} }
    if ($Shape -eq 'alias') {
        Set-Content -LiteralPath (Join-Path $harness 'second.md') -Value 'second payload' -NoNewline
        $entries += @{from='second.md';to='docs/x.md'}
    }
    $manifest = Join-Path $harness 'kit.json'
    @{files=$entries} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest
    $env:MUTHUR_KIT = $kit
    $stdout = Join-Path $case 'stdout.json'
    $stderr = Join-Path $case 'stderr.json'
    & $Cli kit install --harness probe --repo $repoArg 1> $stdout 2> $stderr
    $exit = $LASTEXITCODE
    $err = [string](Get-Content -LiteralPath $stderr -Raw)
    $outsideFiles = @([IO.Directory]::GetFiles($outside, '*', [IO.SearchOption]::AllDirectories))
    $expectedOutside = if ($Shape -eq 'source') {1} else {0}
    Write-Output "MEASURE $Name exit=$exit new-outside-files=$($outsideFiles.Count - $expectedOutside)"
    Assert ($exit -eq $Expected) "$Name expected exit $Expected got $exit : $err"
    Assert ($err -notmatch 'Unhandled exception|muthur!|defect in MUTHUR') "$Name hit a crash/last-resort guard: $err"
    Assert ($outsideFiles.Count -eq $expectedOutside) "$Name changed outside files"
    if ($Expected -eq 2) {
        $errorJson = $err | ConvertFrom-Json
        Assert ($errorJson.code -eq 'invalid_manifest') "$Name wrong error code"
        Assert (-not (Test-Path -LiteralPath (Join-Path $repo '.gitignore'))) "$Name wrote installer file before refusing"
        Assert (@([IO.Directory]::GetFiles((Join-Path $repo 'docs'))).Count -eq 0) "$Name wrote docs before refusing"
        if ($Shape -eq 'source') {
            $real = Join-Path $outside 'source.md'
            $message = "${manifest}: entry 0 ($To) reads `"$From`", which resolves through a link to `"$real`", outside the kit directory."
            Assert ($errorJson.message -eq $message) "$Name wrong source message: $err"
            Assert ((Get-Content -LiteralPath $real -Raw) -eq 'outside source') "$Name changed source"
        } elseif ($Shape -eq 'collision') {
            $message = "${manifest}: entry 0 writes `"inward/x.md`" and entry 1 writes `"docs/x.md/y.md`"; one cannot be both a file and a directory."
            Assert ($errorJson.message -eq $message) "$Name wrong collision message: $err"
        } elseif ($Shape -ne 'traversal') {
            $suffix = ($To -split '/',2)[1]
            $real = if ($Shape -eq 'dangling') {Join-Path (Join-Path $case 'missing') $suffix} else {Join-Path $outside $suffix}
            $message = "${manifest}: entry 0 writes `"$To`", which resolves through a link to `"$real`", outside the repository."
            Assert ($errorJson.message -eq $message) "$Name wrong destination message: $err"
        }
    } else {
        $result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
        Assert ($result.harness -eq 'probe') "$Name wrong success output"
        $file = if ($Shape -eq 'alias') {Join-Path $repo 'docs/x.md'} else {Join-Path $repo 'docs/ok.md'}
        $expectedContent = if ($Shape -eq 'alias') {'second payload'} else {'T57 payload'}
        Assert ((Get-Content -LiteralPath $file -Raw).Contains($expectedContent)) "$Name wrong installed content"
    }
    if ($Shape -eq 'dangling') { Assert (-not (Test-Path -LiteralPath (Join-Path $case 'missing'))) "$Name created dangling target" }
    Write-Output "PASS $Name exit=$exit new-outside-files=$($outsideFiles.Count - $expectedOutside)"
}
try {
    RunCase 'junction-out' 'link/escaped.md' 2
    RunCase 'junction-deeper' 'link/sub/escaped.md' 2
    RunCase 'junction-chain' 'chain/escaped.md' 2
    RunCase 'junction-inward' 'inward/ok.md' 0
    RunCase 'ordinary' 'docs/ok.md' 0
    RunCase 'traversal' '../outside/escaped.md' 2 'traversal'
    RunCase 'source-out' 'docs/ok.md' 2 'source' 'source-link/source.md'
    RunCase 'linked-root' 'docs/ok.md' 0 'repo-link'
    RunCase 'linked-collision' 'inward/x.md' 2 'collision'
    RunCase 'linked-alias' 'inward/x.md' 0 'alias'
    RunCase 'dangling-out' 'dangling/x.md' 2 'dangling'
    Remove-Item Env:MUTHUR_KIT
    foreach ($harness in @('claude','codex','generic')) {
        $repo = Join-Path $scratch ('shipped-' + $harness)
        New-Item -ItemType Directory -Path $repo | Out-Null
        $manifest = Get-Content -LiteralPath (Join-Path (Split-Path $Cli) "kit/$harness/kit.json") -Raw | ConvertFrom-Json
        $output = & $Cli kit install --harness $harness --repo $repo
        Assert ($LASTEXITCODE -eq 0) "shipped $harness failed"
        foreach ($entry in $manifest.files) { Assert (Test-Path -LiteralPath (Join-Path $repo $entry.to) -PathType Leaf) "shipped $harness missing $($entry.to)" }
        $result = $output | ConvertFrom-Json
        Assert ($result.files.Count -eq ($manifest.files.Count + 2)) "shipped $harness count mismatch"
        Write-Output "PASS shipped-$harness exit=0 manifest-files=$($manifest.files.Count) installed-files=$($result.files.Count)"
    }
} finally {
    try {
        for ($index = $links.Count - 1; $index -ge 0; $index--) {
            $full = [IO.Path]::GetFullPath($links[$index])
            if (-not $full.StartsWith($scratch + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe link cleanup path' }
            [IO.Directory]::Delete($full)
        }
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
        Assert (-not (Test-Path -LiteralPath $scratch)) 'Scratch cleanup failed'
        Write-Output 'CLEANUP scratch removed; all junctions removed before recursive cleanup'
    } finally {
        foreach ($name in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
        }
    }
}
