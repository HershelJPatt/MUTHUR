param([Parameter(Mandatory)][string]$Cli)
$ErrorActionPreference = 'Stop'
$Cli = [IO.Path]::GetFullPath($Cli)
if (-not [IO.File]::Exists($Cli)) { throw "Installed CLI does not exist: $Cli" }
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$scratch = [IO.Path]::GetFullPath((Join-Path $artifacts ('t83-verification-' + [guid]::NewGuid().ToString('n'))))
$allowed = $artifacts + [IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe scratch path' }
$links = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Bytes([string]$Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
function Snapshot([string]$Root) {
    $result = @{}
    foreach ($file in [IO.Directory]::GetFiles($Root, '*', [IO.SearchOption]::AllDirectories)) {
        $result[[IO.Path]::GetRelativePath($Root, $file)] = Bytes $file
    }
    foreach ($directory in [IO.Directory]::GetDirectories($Root, '*', [IO.SearchOption]::AllDirectories)) {
        $result[([IO.Path]::GetRelativePath($Root, $directory) + '/')] = '<directory>'
    }
    return $result
}
function RunCase([string]$Name, [string]$Mode = 'replace', [string]$Shape = 'external', [bool]$Allowed = $false) {
    $case = Join-Path $scratch $Name
    $repo = Join-Path $case 'repo'
    $kit = Join-Path $case 'kit'
    $harness = Join-Path $kit 'probe'
    foreach ($dir in @($repo,$harness)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
    $source = Join-Path $harness 'source.md'
    [IO.File]::WriteAllText($source, "new payload" + [char]10)
    $to = if ($Shape -in @('ignore','ignore-noop')) { '.gitignore' }
        elseif ($Shape -in @('project','project-explicit')) { 'muthur.project.json' } else { 'é-文件.md' }
    $target = Join-Path $repo $to
    $original = if ($Shape -eq 'equal') { "new payload" + [char]13 + [char]10 }
        elseif ($Shape -eq 'section-noop') { "<!-- BEGIN MUTHUR -->" + [char]10 + "new payload" + [char]10 + "<!-- END MUTHUR -->" + [char]10 }
        elseif ($Shape -eq 'ignore-noop') { ".worktrees/" + [char]13 + [char]10 }
        else { "original bytes" + [char]13 + [char]10 }
    [IO.File]::WriteAllText($target, $original)
    $alias = if ($Shape -eq 'internal') { Join-Path $repo 'alias.md' } else { Join-Path $case 'outside.md' }
    if ($Shape -ne 'normal') {
        New-Item -ItemType HardLink -Path $alias -Target $target | Out-Null
        $links.Add($alias)
        Assert ([IO.File]::Exists($alias)) "$Name failed to create hard-link fixture"
    }
    $entries = @()
    if ($Shape -in @('late','normal')) { $entries += @{from='source.md';to='new/nested/safe.md'} }
    if ($Shape -in @('ignore','ignore-noop','project')) {
        $entries += @{from='source.md';to='new/nested/safe.md'}
    } else {
        $entry = @{from='source.md';to=$to}
        if ($Mode -ne 'default') { $entry.mode = $Mode }
        $entries += $entry
    }
    $manifest = Join-Path $harness 'kit.json'
    [IO.File]::WriteAllText($manifest, (@{files=$entries} | ConvertTo-Json -Depth 5))
    $before = Snapshot $case
    $start = [Diagnostics.ProcessStartInfo]::new($Cli)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in @('kit','install','--harness','probe','--repo',$repo)) { $start.ArgumentList.Add($arg) }
    $start.Environment['MUTHUR_HOME'] = Join-Path $scratch 'home'
    $start.Environment['MUTHUR_URL'] = 'http://127.0.0.1:1'
    $start.Environment['MUTHUR_KIT'] = $kit
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw "$Name CLI timed out"
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        $expected = if ($Allowed) { 0 } else { 2 }
        Assert ($process.ExitCode -eq $expected) "$Name expected exit $expected got $($process.ExitCode): $err"
        if (-not $Allowed) {
            $errorJson = $err | ConvertFrom-Json
            Assert ($errorJson.code -eq 'invalid_manifest') "$Name wrong error code"
            $prefix = if ($Shape -eq 'ignore') { 'kit install' }
                elseif ($Shape -eq 'late') { "$($manifest): entry 1" } else { "$($manifest): entry 0" }
            $message = "$prefix writes ""$to"", which has 2 hard links; replace it with an independent file before installing."
            Assert ($errorJson.message -eq $message) "$Name wrong diagnostic: $err"
            $after = Snapshot $case
            Assert ($before.Count -eq $after.Count) "$Name added files or directories"
            foreach ($key in $before.Keys) { Assert ($after[$key] -ceq $before[$key]) "$Name changed $key" }
        } else {
            Assert (($out | ConvertFrom-Json).harness -eq 'probe') "$Name missing install result"
            Assert ([IO.File]::Exists((Join-Path $repo '.gitignore'))) "$Name missing .gitignore"
            Assert ([IO.File]::Exists((Join-Path $repo 'muthur.project.json'))) "$Name missing project file"
            if ($Shape -eq 'normal') {
                Assert ([IO.File]::ReadAllText($target) -ceq [IO.File]::ReadAllText($source)) "$Name did not replace"
                Assert ([IO.File]::ReadAllText((Join-Path $repo 'new/nested/safe.md')) -ceq [IO.File]::ReadAllText($source)) "$Name did not create"
            } else {
                Assert ([IO.File]::ReadAllText($target) -ceq $original) "$Name changed kept destination"
                Assert ([IO.File]::ReadAllText($alias) -ceq $original) "$Name changed alias"
            }
            Assert ((Bytes $source) -ceq $before[[IO.Path]::GetRelativePath($case, $source)]) "$Name changed source"
        }
        Write-Output "PASS $Name exit=$expected"
    } finally { $process.Dispose() }
}
try {
    Write-Output "Native host: $([Runtime.InteropServices.RuntimeInformation]::OSDescription) / $([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)"
    RunCase 'external-replace'
    RunCase 'external-default' 'default'
    RunCase 'external-unknown' 'unknown'
    RunCase 'external-section' 'section'
    RunCase 'internal' 'replace' 'internal'
    RunCase 'kept-create' 'create' 'external' $true
    RunCase 'equal-crlf' 'replace' 'equal' $true
    RunCase 'section-noop' 'section' 'section-noop' $true
    RunCase 'ignore-refusal' 'replace' 'ignore'
    RunCase 'ignore-noop' 'replace' 'ignore-noop' $true
    RunCase 'project-preserved' 'replace' 'project' $true
    RunCase 'project-explicit' 'replace' 'project-explicit'
    RunCase 'late-refusal' 'replace' 'late'
    RunCase 'normal' 'replace' 'normal' $true
} finally {
    $checked = [IO.Path]::GetFullPath($scratch)
    if (-not $checked.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($checked) -notlike 't83-verification-*') { throw 'Unsafe cleanup path' }
    foreach ($link in $links) {
        if (-not [IO.Path]::GetFullPath($link).StartsWith($checked + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe fixture unlink path'
        }
        [IO.File]::Delete($link)
    }
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
