<#
.SYNOPSIS
  Publishes muthur.exe (Native AOT) and Muthur.Server into one install directory.
.EXAMPLE
  ./scripts/install.ps1                # -> %LOCALAPPDATA%\Muthur\bin
  ./scripts/install.ps1 -AddToPath     # also appends the directory to the user PATH
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Muthur\bin'),
    [switch]$AddToPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$existing = Join-Path $Destination 'muthur.exe'
if (Test-Path $existing) {
    # The running server locks its own files; stop it before overwriting.
    & $existing down | Out-Null
    Start-Sleep -Milliseconds 800
}

# The Native AOT targets locate the MSVC linker through vswhere.exe, which is not on PATH by default.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and (($env:Path -split ';') -notcontains $vsInstaller)) { $env:Path = "$vsInstaller;$env:Path" }

dotnet publish (Join-Path $repo 'src\Muthur.Cli') -c Release -r win-x64 -o $Destination --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed (Native AOT needs the VS "Desktop development with C++" workload).' }

dotnet publish (Join-Path $repo 'src\Muthur.Server') -c Release -o (Join-Path $Destination 'server') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

if ($AddToPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $Destination) {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$Destination", 'User')
        Write-Host "Added $Destination to the user PATH (open a new terminal)."
    }
}
Write-Host "Installed to $Destination"
