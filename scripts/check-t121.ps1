param([ValidateSet('build','test')][string]$Check = 'test', [string]$Filter, [switch]$Serialized, [string]$Project)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$name = if ($Filter) { 'focused' } else { $Check }
if ($Project) { $name += '-project' }
if ($Serialized) { $name += '-serialized' }
$log = Join-Path $repo "t121-$name.log"
$errorLog = Join-Path $repo "t121-$name.stderr.log"
$arguments = if ($Check -eq 'build') { @('build','--nologo','--disable-build-servers') } else { @('test','-v','n','--disable-build-servers','--blame-hang-timeout','3m') }
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($Project) { $arguments += $Project }
if ($Serialized) { $arguments += '-m:1' }
$previousProcessors = $env:DOTNET_PROCESSOR_COUNT
$process = $null
try {
    if ($Serialized) { $env:DOTNET_PROCESSOR_COUNT = '4' }
    & muthur agent heartbeat --summary "T-121: running bounded $Check checks under founder 53; no implementation children."
    $process = Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError $errorLog
    $deadline = [DateTime]::UtcNow.AddMinutes(20)
    $heartbeatAt = [DateTime]::UtcNow.AddMinutes(3)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "$Check exceeded 20 minutes" }
        if ([DateTime]::UtcNow -ge $heartbeatAt) {
            & muthur agent heartbeat --summary "T-121: deterministic $Check runner remains active."
            $heartbeatAt = [DateTime]::UtcNow.AddMinutes(3)
        }
    }
    $code = $process.ExitCode
    Get-Content -LiteralPath $errorLog | Add-Content -LiteralPath $log
    & muthur utility summarize --file $log --task T-121
    & muthur agent heartbeat --summary "T-121: $Check finished, exit $code; reviewing evidence."
    exit $code
}
finally {
    $env:DOTNET_PROCESSOR_COUNT = $previousProcessors
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
    if ($process) { $process.Dispose() }
}
