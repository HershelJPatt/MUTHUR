# Verify T-55 against two installed Windows CLIs; results are retained under artifacts/.
# pwsh ./scripts/verify-t55.ps1 -Cli ./artifacts/t55/muthur.exe -BaseCli ./artifacts/t55-before/muthur.exe
param(
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$BaseCli
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This harness measures Windows FileShare.None and read-only behavior.' }
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$BaseCli = (Resolve-Path -LiteralPath $BaseCli).Path
$currentKit = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../kit')).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = [IO.Path]::GetFullPath((Join-Path $tempRoot ('t55-unit-b-' + [Guid]::NewGuid().ToString('N'))))
$evidence = Join-Path (Join-Path $PSScriptRoot '../artifacts') ('t55-unit-b-results-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$savedEnv = @{}
foreach ($name in 'MUTHUR_KIT','MUTHUR_HOME','MUTHUR_URL') { $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:MUTHUR_HOME = Join-Path $scratch 'home'
$env:MUTHUR_URL = 'http://127.0.0.1:1'
$rows = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[string]]::new()

function Put([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::UTF8.GetBytes($Content))
}
function Snapshot([string]$Repo) {
    $lines = @(Get-ChildItem -LiteralPath $Repo -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Repo, $_.FullName).Replace('\','/')
        if ($_.PSIsContainer) { "D $relative/" }
        else { "F $relative bytes=$($_.Length) sha256=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    })
    return ($lines -join "`n")
}
function RunCli([string]$Binary, [string]$Repo, [string]$Kit, [string]$Harness, [string]$Label) {
    $env:MUTHUR_KIT = $Kit
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Binary
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $scratch
    foreach ($arg in @('kit','install','--harness',$Harness,'--repo',$Repo,'--project-key','t55-verification')) { $psi.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    try {
        if (-not $process.Start()) { throw 'Failed to start CLI.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw "CLI timed out: $Label" }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        Put (Join-Path $evidence "$Label.stdout.txt") $out
        Put (Join-Path $evidence "$Label.stderr.txt") $err
        return [pscustomobject]@{ Exit = $process.ExitCode; Stdout = $out; Stderr = $err; Crash = ($err -match 'Unhandled exception|muthur!') }
    }
    finally { $process.Dispose() }
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $issues.Add($Message) }
}
function AclCase([string]$Name, [bool]$RollbackBlocked) {
    $f = Fixture $Name
    $docs = Join-Path $f.Repo 'docs'
    [IO.Directory]::CreateDirectory($docs) | Out-Null
    Put (Join-Path $f.Repo '.gitignore') ".worktrees/`r`nlocal/`r`n"
    if (-not $RollbackBlocked) {
        $f.Entries = @(@{from='a.md';to='docs/a.md'},@{from='b.md';to='docs/b.md'},@{from='c.md';to='docs/c.md'})
        Put (Join-Path $docs 'b.md') "ORIGINAL DOCS B`r`nlast"
    }
    Put (Join-Path $f.Kit 'scratch/kit.json') (@{harness='scratch';files=$f.Entries} | ConvertTo-Json -Depth 6)
    $before = Snapshot $f.Repo
    Put (Join-Path $evidence "$Name.before.txt") $before
    $aclPath = if ($RollbackBlocked) { $docs } else { $f.Repo }
    # Separate ACL objects: modifying the working copy must not alter the restore object.
    $originalAcl = Get-Acl -LiteralPath $aclPath
    $workingAcl = Get-Acl -LiteralPath $aclPath
    $principal = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $handle = $null
    try {
        if ($RollbackBlocked) {
            $workingAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny))
            $workingAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, [Security.AccessControl.FileSystemRights]::Delete, [Security.AccessControl.InheritanceFlags]::ObjectInherit, [Security.AccessControl.PropagationFlags]::InheritOnly, [Security.AccessControl.AccessControlType]::Deny))
            $handle = [IO.File]::Open((Join-Path $f.Repo 'c.md'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        else {
            $workingAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, [Security.AccessControl.FileSystemRights]::CreateFiles, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny))
        }
        Set-Acl -LiteralPath $aclPath -AclObject $workingAcl
        Put (Join-Path $evidence "$Name.acl.txt") ((Get-Acl -LiteralPath $aclPath).Sddl)
        $result = RunCli $Cli $f.Repo $f.Kit 'scratch' $Name
    }
    finally {
        if ($null -ne $handle) { $handle.Dispose() }
        # ACL reset also removes inherited denial on created child files before cleanup.
        Set-Acl -LiteralPath $aclPath -AclObject $originalAcl
    }
    $after = Snapshot $f.Repo
    Put (Join-Path $evidence "$Name.after.txt") $after
    Check ($result.Exit -eq 1) "$Name expected exit 1, got $($result.Exit)"
    Check (-not $result.Crash) "$Name raw stderr contains crash marker"
    Check ([string]::IsNullOrWhiteSpace($result.Stdout)) "$Name emitted partial success stdout"
    $errorBody = $result.Stderr | ConvertFrom-Json
    if ($RollbackBlocked) {
        Check ($errorBody.code -eq 'install_not_undone') "$Name expected install_not_undone, got $($errorBody.code)"
        Check ($errorBody.message.Contains('while installing "c.md"')) "$Name failed-file clause incorrect"
        Check ($errorBody.message.Contains('docs\a.md could not be restored')) "$Name did not name retained file"
        $retained = Join-Path $docs 'a.md'
        Check ([IO.File]::Exists($retained)) "$Name fixture failed to retain created file"
        if ([IO.File]::Exists($retained)) {
            Check ([IO.File]::ReadAllText($retained).Equals('a from the kit')) "$Name retained file content incorrect"
            [IO.File]::Delete($retained)
        }
        Check ($before -ceq (Snapshot $f.Repo)) "$Name other paths/bytes were not restored"
    }
    else {
        Check ($errorBody.code -eq 'install_failed') "$Name expected install_failed, got $($errorBody.code)"
        Check ($errorBody.message.Contains('while installing "muthur.project.json"')) "$Name did not reach project seed"
        Check ($before -ceq $after) "$Name paths/bytes changed"
    }
    $rerun = RunCli $Cli $f.Repo $f.Kit 'scratch' "$Name.rerun"
    Check ($rerun.Exit -eq 0 -and -not $rerun.Crash) "$Name rerun failed"
    Put (Join-Path $evidence "$Name.rerun.tree.txt") (Snapshot $f.Repo)
    $rows.Add([pscustomobject]@{Case=$Name;Exit=$result.Exit;Code=$errorBody.code;Crash=$result.Crash;TreeIdentical=($before -ceq $after);RerunExit=$rerun.Exit})
}
function Fixture([string]$Name) {
    $root = Join-Path $scratch $Name
    $repo = Join-Path $root 'repo'
    $kit = Join-Path $root 'kit'
    [IO.Directory]::CreateDirectory($repo) | Out-Null
    Put (Join-Path $kit 'core/dummy.md') 'included content'
    foreach ($n in 'a','b','c') { Put (Join-Path $kit "scratch/$n.md") "$n from the kit" }
    Put (Join-Path $repo 'b.md') "ORIGINAL B`r`nsecond line"
    Put (Join-Path $repo 'c.md') 'ORIGINAL C'
    $entries = @(@{from='a.md';to='docs/a.md'},@{from='b.md';to='b.md'},@{from='c.md';to='c.md'})
    Put (Join-Path $kit 'scratch/kit.json') (@{harness='scratch';files=$entries} | ConvertTo-Json -Depth 6)
    return [pscustomobject]@{Root=$root;Repo=$repo;Kit=$kit;Entries=$entries}
}
function FailureCase([string]$Name, [scriptblock]$Setup, [int]$ExpectedExit, [string]$ExpectedCode, [string]$ExpectedName, [string]$Mode = 'lock') {
    $f = Fixture $Name
    $config = & $Setup $f
    Put (Join-Path $f.Kit 'scratch/kit.json') (@{harness='scratch';files=$f.Entries} | ConvertTo-Json -Depth 6)
    if ($config.ContainsKey('Installed')) {
        $initial = RunCli $Cli $f.Repo $f.Kit 'scratch' "$Name.initial"
        Check ($initial.Exit -eq 0) "$Name initial install exit $($initial.Exit)"
        Check (-not $initial.Crash) "$Name initial install crashed"
    }
    $before = Snapshot $f.Repo
    Put (Join-Path $evidence "$Name.before.txt") $before
    $handle = $null
    $attributes = $null
    try {
        if ($Mode -eq 'lock') { $handle = [IO.File]::Open($config.Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        elseif ($Mode -eq 'readonly') { $attributes = [IO.File]::GetAttributes($config.Path); [IO.File]::SetAttributes($config.Path, $attributes -bor [IO.FileAttributes]::ReadOnly) }
        $result = RunCli $Cli $f.Repo $f.Kit 'scratch' $Name
    }
    finally {
        if ($null -ne $handle) { $handle.Dispose() }
        if ($null -ne $attributes) { [IO.File]::SetAttributes($config.Path, $attributes) }
    }
    $after = Snapshot $f.Repo
    Put (Join-Path $evidence "$Name.after.txt") $after
    $same = $before -ceq $after
    Check ($result.Exit -eq $ExpectedExit) "$Name expected exit $ExpectedExit, got $($result.Exit)"
    Check (-not $result.Crash) "$Name raw stderr contains crash marker"
    Check $same "$Name repository paths/bytes changed"
    Check ([string]::IsNullOrWhiteSpace($result.Stdout)) "$Name emitted partial success stdout"
    $errorBody = $null
    try { $errorBody = $result.Stderr | ConvertFrom-Json } catch { $issues.Add("$Name stderr is not JSON: $_") }
    if ($null -ne $errorBody) {
        Check ($errorBody.code -eq $ExpectedCode) "$Name expected $ExpectedCode, got $($errorBody.code)"
        $wanted = if ($config.ContainsKey('ExpectedName')) { $config.ExpectedName } else { $ExpectedName }
        Check ($errorBody.message.Contains($wanted)) "$Name error does not name $wanted"
        if ($ExpectedExit -eq 1) {
            Check ($errorBody.message.Contains(('while installing "' + $wanted + '"'))) "$Name installing clause does not name $wanted"
            Check ($errorBody.message.Contains('put back the way it was found')) "$Name missing rollback assurance"
        }
        if ($config.ContainsKey('Include')) {
            Check (-not $errorBody.message.Contains('b.md')) "$Name blamed preceding b.md"
            Check ($errorBody.message.Contains($config.Path)) "$Name exception does not name include target"
        }
    }
    if ($Mode -eq 'directory') { [IO.Directory]::Delete($config.Path) }
    $rerun = RunCli $Cli $f.Repo $f.Kit 'scratch' "$Name.rerun"
    Check ($rerun.Exit -eq 0) "$Name rerun exit $($rerun.Exit)"
    Check (-not $rerun.Crash) "$Name rerun crashed"
    Check ([IO.File]::Exists((Join-Path $f.Repo 'muthur.project.json'))) "$Name rerun has no project file"
    Check ([IO.File]::Exists((Join-Path $f.Repo '.gitignore'))) "$Name rerun has no .gitignore"
    foreach ($entry in $f.Entries) { Check ([IO.File]::Exists((Join-Path $f.Repo $entry.to))) "$Name rerun missing $($entry.to)" }
    Put (Join-Path $evidence "$Name.rerun.tree.txt") (Snapshot $f.Repo)
    $rows.Add([pscustomobject]@{Case=$Name;Exit=$result.Exit;Code=if($errorBody){$errorBody.code}else{'INVALID JSON'};Crash=$result.Crash;TreeIdentical=$same;RerunExit=$rerun.Exit})
}
try {
    FailureCase 'lock-first' { param($f) Put (Join-Path $f.Repo 'docs/a.md') "FIRST`r`noriginal"; @{Path=(Join-Path $f.Repo 'docs/a.md')} } 1 'install_failed' 'docs\a.md'
    FailureCase 'lock-middle' { param($f) @{Path=(Join-Path $f.Repo 'b.md')} } 1 'install_failed' 'b.md'
    FailureCase 'lock-last' { param($f) @{Path=(Join-Path $f.Repo 'c.md')} } 1 'install_failed' 'c.md'
    FailureCase 'readonly-destination' { param($f) @{Path=(Join-Path $f.Repo 'c.md')} } 2 'invalid_manifest' 'c.md' 'readonly'
    FailureCase 'lock-gitignore' { param($f) Put (Join-Path $f.Repo '.gitignore') "local/`r`nprivate/`r`n"; @{Path=(Join-Path $f.Repo '.gitignore')} } 1 'install_failed' '.gitignore'
    FailureCase 'readonly-gitignore' { param($f) Put (Join-Path $f.Repo '.gitignore') "local/`r`nprivate/`r`n"; @{Path=(Join-Path $f.Repo '.gitignore')} } 2 'invalid_manifest' '.gitignore' 'readonly'
    FailureCase 'directory-gitignore' { param($f) $p=Join-Path $f.Repo '.gitignore'; [IO.Directory]::CreateDirectory($p) | Out-Null; @{Path=$p} } 2 'invalid_manifest' '.gitignore' 'directory'
    FailureCase 'directory-project-seed' { param($f) $p=Join-Path $f.Repo 'muthur.project.json'; [IO.Directory]::CreateDirectory($p) | Out-Null; @{Path=$p} } 2 'invalid_manifest' 'muthur.project.json' 'directory'
    FailureCase 'create-section-crlf' {
        param($f)
        Put (Join-Path $f.Repo 'kept.md') "USER KEEP`r`nno trailing newline"
        Put (Join-Path $f.Repo 'AGENTS.md') "USER INTRO`r`n<!-- BEGIN MUTHUR -->`r`nOLD SECTION`r`n<!-- END MUTHUR -->`r`nUSER END"
        $f.Entries = @(@{from='a.md';to='kept.md';mode='create'},@{from='a.md';to='new/create.md';mode='create'},@{from='a.md';to='AGENTS.md';mode='section'},@{from='b.md';to='b.md'},@{from='c.md';to='c.md'})
        @{Path=(Join-Path $f.Repo 'c.md')}
    } 1 'install_failed' 'c.md'
    FailureCase 'already-installed' { param($f) @{Path=(Join-Path $f.Repo 'c.md');Installed=$true} } 1 'install_failed' 'c.md'
    FailureCase 'three-deep-directories' { param($f) $f.Entries[0].to='one/two/three/a.md'; @{Path=(Join-Path $f.Repo 'c.md')} } 1 'install_failed' 'c.md'
    FailureCase 'locked-entry-source-t82' { param($f) $p=Join-Path $f.Kit 'scratch/c.md'; @{Path=$p;ExpectedName=$p} } 2 'invalid_manifest' ''
    FailureCase 'locked-include-target' {
        param($f)
        Put (Join-Path $f.Kit 'scratch/c.md') '{{core:dummy.md}}'
        @{Path=(Join-Path $f.Kit 'core/dummy.md');ExpectedName=(Join-Path $f.Kit 'scratch/c.md');Include=$true}
    } 1 'install_failed' ''

    AclCase 'acl-project-seed-write' $false
    AclCase 'acl-rollback-not-undone' $true

    foreach ($harness in 'claude','codex','generic') {
        $newRepo = Join-Path $scratch "shipped-$harness-current"
        $oldRepo = Join-Path $scratch "shipped-$harness-base"
        [IO.Directory]::CreateDirectory($newRepo) | Out-Null
        [IO.Directory]::CreateDirectory($oldRepo) | Out-Null
        Put (Join-Path $evidence "shipped-$harness-current.before.txt") (Snapshot $newRepo)
        Put (Join-Path $evidence "shipped-$harness-base.before.txt") (Snapshot $oldRepo)
        $new = RunCli $Cli $newRepo $currentKit $harness "shipped-$harness-current"
        $old = RunCli $BaseCli $oldRepo $currentKit $harness "shipped-$harness-base"
        $newTree = Snapshot $newRepo
        $oldTree = Snapshot $oldRepo
        Put (Join-Path $evidence "shipped-$harness-current.after.txt") $newTree
        Put (Join-Path $evidence "shipped-$harness-base.after.txt") $oldTree
        Check ($new.Exit -eq 0 -and $old.Exit -eq 0) "shipped-$harness current/base install did not both exit 0"
        Check (-not $new.Crash -and -not $old.Crash) "shipped-$harness current/base crashed"
        Check ($newTree -ceq $oldTree) "shipped-$harness current/base file paths/bytes differ"
        if ($new.Exit -eq 0 -and $old.Exit -eq 0) {
            $newJson = $new.Stdout | ConvertFrom-Json
            $oldJson = $old.Stdout | ConvertFrom-Json
            $newJson.repo = '<same-repo>'
            $oldJson.repo = '<same-repo>'
            Check (($newJson | ConvertTo-Json -Depth 30 -Compress) -ceq ($oldJson | ConvertTo-Json -Depth 30 -Compress)) "shipped-$harness result JSON differs beyond repo path"
        }
        $rows.Add([pscustomobject]@{Case="shipped-$harness";Exit=$new.Exit;Code='success';Crash=($new.Crash -or $old.Crash);TreeIdentical=($newTree -ceq $oldTree);RerunExit=$null;BaseExit=$old.Exit})
    }
    $limitations = @'
ACL fixtures exercise post-preflight project seed failure and incomplete rollback without timed races. They require permission to adjust ACLs on the harness-owned scratch directory. Original ACLs are restored in finally before cleanup. An ACL setup failure is a harness failure, not a skipped or passing product case.
The incomplete rollback row deliberately has TreeIdentical=False: docs/a.md cannot be deleted under its temporary ACL. The harness asserts it is named in install_not_undone and all other bytes are restored, then removes only that known fixture file after resetting ACLs and verifies the clean rerun.
Shipped-kit comparisons use the identical current kit directory for both binaries and a fixed --project-key, so all paths and bytes (including muthur.project.json) are compared. Result JSON is compared after normalizing only the repo path.
'@
    Put (Join-Path $evidence 'limitations.txt') $limitations
    Put (Join-Path $evidence 'summary.json') ($rows.ToArray() | ConvertTo-Json -Depth 10)
    Put (Join-Path $evidence 'assertion-failures.txt') ($issues.ToArray() -join "`n")
    Put (Join-Path $evidence 'provenance.json') (@{Cli=$Cli;CliSHA256=(Get-FileHash -LiteralPath $Cli).Hash;BaseCli=$BaseCli;BaseCliSHA256=(Get-FileHash -LiteralPath $BaseCli).Hash;IdenticalKit=$currentKit;ProjectKey='t55-verification';Scratch=$scratch;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json)
    Write-Output ($rows.ToArray() | Format-Table -AutoSize | Out-String -Width 240)
    Write-Output "Evidence: $evidence"
    Write-Output $limitations
    if ($issues.Count -gt 0) { throw ($issues.ToArray() -join "`n") }
}
finally {
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (-not $resolvedScratch.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedScratch) -notmatch '^t55-unit-b-[0-9a-f]{32}$') { throw "Refusing cleanup outside verified scratch containment: $resolvedScratch" }
    if ([IO.Directory]::Exists($resolvedScratch)) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
