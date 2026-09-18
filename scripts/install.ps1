<#
.SYNOPSIS
  Publishes muthur.exe (Native AOT) and Muthur.Server into one install directory.
.EXAMPLE
  ./scripts/install.ps1                # -> %LOCALAPPDATA%\Muthur\bin
  ./scripts/install.ps1 -AddToPath     # also appends the directory to the user PATH
  ./scripts/install.ps1 -RestartRunning   # upgrade the live hub in place
  ./scripts/install.ps1 -Ref main      # publish a pinned commit instead of the working tree
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Muthur\bin'),
    [switch]$AddToPath,
    # Required when a hub is currently running from the destination (it is stopped, replaced and started again).
    [switch]$RestartRunning,
    # Publish this commit from a temporary detached worktree instead of whatever the checkout happens to be on.
    [string]$Ref
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Normalise so the running-hub check below cannot be bypassed by a differently spelled path (.\, relative, trailing slash).
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination).TrimEnd([char[]]'\/')

# Provenance is evidence, not a gate: git is asked, its exit code inspected, and a failure means
# "this is not a repository" rather than an install failure, whatever the host's native-error settings are.
function Invoke-Git([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $out = & git @Arguments 2>$null
    [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Lines = @($out); Text = ((@($out) -join "`n").Trim()) }
}

# The ref, short commit and linked-worktree path of a checkout, or $null when it is not a repository.
function Get-Provenance([string]$Path) {
    $head = Invoke-Git @('-C', $Path, 'rev-parse', '--abbrev-ref', 'HEAD')
    if (-not $head.Ok) { return $null }
    $name = $head.Text
    if ($name -eq 'HEAD') {
        # Detached, so name it with something a reader can look up rather than the word HEAD.
        $described = Invoke-Git @('-C', $Path, 'describe', '--all', '--always', 'HEAD')
        if ($described.Ok -and $described.Text) { $name = $described.Text }
    }
    $commit = Invoke-Git @('-C', $Path, 'rev-parse', '--short', 'HEAD')
    $gitDir = Invoke-Git @('-C', $Path, 'rev-parse', '--git-dir')
    $common = Invoke-Git @('-C', $Path, 'rev-parse', '--git-common-dir')
    [pscustomobject]@{
        Ref = $name
        Commit = $commit.Text
        # A linked worktree keeps its own git dir under the common one; in the main worktree the two are equal.
        Linked = ($gitDir.Ok -and $common.Ok -and $gitDir.Text -ne $common.Text)
    }
}

# Everything that can refuse happens here, before the first publish: a refusal that arrives after a
# two-minute build has already cost what it was meant to save.
$source = $repo
$worktree = $null
if ($Ref) {
    if (-not (Invoke-Git @('-C', $repo, 'rev-parse', '--verify', '--quiet', "$Ref^{commit}")).Ok) {
        throw "'$Ref' is not a commit in $repo."
    }
    $worktree = Join-Path ([IO.Path]::GetTempPath()) ('muthur-ref-' + [guid]::NewGuid().ToString('n'))
    if (-not (Invoke-Git @('-C', $repo, 'worktree', 'add', '--detach', $worktree, $Ref)).Ok) {
        throw "Could not create a worktree for '$Ref' in $repo."
    }
    $source = $worktree
}
else {
    # Untracked files must not count: bin/, obj/ and artifacts/ are untracked by design, so counting
    # them would fail every run. A tracked edit is the real hazard -- it builds a binary that
    # corresponds to no commit, which neither the ledger nor a validator's evidence can name.
    $dirty = Invoke-Git @('-C', $source, 'status', '--porcelain', '--untracked-files=no')
    if ($dirty.Ok -and $dirty.Text) {
        $refusal = @(
            "The working tree at $source has uncommitted changes, so the build would correspond to no commit.",
            'Commit them, or pass -Ref <ref> to publish a named commit instead.'
        ) + (@($dirty.Lines) | Select-Object -First 10)
        throw ($refusal -join [Environment]::NewLine)
    }
}

$provenance = Get-Provenance $source
$published =
    if ($Ref) { "Published from $Ref ($($provenance.Commit))." }
    elseif (-not $provenance) { "Published from a non-git directory $source." }
    elseif ($provenance.Linked) { "Published from $($provenance.Ref) ($($provenance.Commit)) in worktree $source." }
    else { "Published from $($provenance.Ref) ($($provenance.Commit))." }

try {
    $existing = Join-Path $Destination 'muthur.exe'
    $serverDir = Join-Path $Destination 'server'
    $running = Get-Process -Name 'Muthur.Server' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($serverDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        # A hub is running from this very directory. Replacing it is a deliberate act, never a side effect:
        # a validator who forgets -Destination must not take the organization's live hub down.
        if (-not $RestartRunning) {
            throw "A hub is running from $Destination. Pass -RestartRunning to stop, upgrade and restart it, or -Destination <dir> to install elsewhere."
        }
        & $existing down | Out-Null
    }

    # The Native AOT targets locate the MSVC linker through vswhere.exe, which is not on PATH by default.
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $vsInstaller) -and (($env:Path -split ';') -notcontains $vsInstaller)) { $env:Path = "$vsInstaller;$env:Path" }

    dotnet publish (Join-Path $source 'src\Muthur.Cli') -c Release -r win-x64 -o $Destination --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed (Native AOT needs the VS "Desktop development with C++" workload).' }

    dotnet publish (Join-Path $source 'src\Muthur.Server') -c Release -o (Join-Path $Destination 'server') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

    $kit = Join-Path $Destination 'kit'
    if (Test-Path $kit) { Remove-Item $kit -Recurse -Force }
    Copy-Item (Join-Path $source 'kit') $kit -Recurse

    if ($AddToPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $Destination) {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$Destination", 'User')
            Write-Host "Added $Destination to the user PATH (open a new terminal)."
        }
    }
    if ($running) { & (Join-Path $Destination 'muthur.exe') up | Out-Null; Write-Host 'Hub restarted.' }
    Write-Host $published
    Write-Host "Installed to $Destination"
}
finally {
    # A failed publish must leave no worktree behind.
    if ($worktree -and (Test-Path $worktree)) {
        Invoke-Git @('-C', $repo, 'worktree', 'remove', '--force', $worktree) | Out-Null
    }
}
