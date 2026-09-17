<#
.SYNOPSIS
  Publishes muthur.exe (Native AOT) and Muthur.Server into one install directory.
.EXAMPLE
  ./scripts/install.ps1                # -> %LOCALAPPDATA%\Muthur\bin
  ./scripts/install.ps1 -AddToPath     # also appends the directory to the user PATH
  ./scripts/install.ps1 -RestartRunning   # upgrade the live hub in place
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Muthur\bin'),
    [switch]$AddToPath,
    # Required when a hub is currently running from the destination (it is stopped, replaced and started again).
    [switch]$RestartRunning
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$existing = Join-Path $Destination 'muthur.exe'
$serverDir = Join-Path $Destination 'server'
$running = Get-Process -Name 'Muthur.Server' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($serverDir, [StringComparison]::OrdinalIgnoreCase) }
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

dotnet publish (Join-Path $repo 'src\Muthur.Cli') -c Release -r win-x64 -o $Destination --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed (Native AOT needs the VS "Desktop development with C++" workload).' }

dotnet publish (Join-Path $repo 'src\Muthur.Server') -c Release -o (Join-Path $Destination 'server') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

$kit = Join-Path $Destination 'kit'
if (Test-Path $kit) { Remove-Item $kit -Recurse -Force }
Copy-Item (Join-Path $repo 'kit') $kit -Recurse

if ($AddToPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $Destination) {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$Destination", 'User')
        Write-Host "Added $Destination to the user PATH (open a new terminal)."
    }
}
if ($running) { & (Join-Path $Destination 'muthur.exe') up | Out-Null; Write-Host 'Hub restarted.' }
Write-Host "Installed to $Destination"
