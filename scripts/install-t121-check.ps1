$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$install = Join-Path $repo 'artifacts/t121-verify'
$log = Join-Path $repo 't121-install.log'
$errorLog = Join-Path $repo 't121-install.stderr.log'
$process = $null
try {
    & muthur agent heartbeat --summary 'T-121: bounded isolated AOT publish; no live installation.'
    $info = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $info.WorkingDirectory=$repo; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($arg in @('-NoProfile','-File',(Join-Path $repo 'scripts/install.ps1'),'-Destination',$install)) { $info.ArgumentList.Add($arg) }
    $info.Environment['MUTHUR_HOME'] = Join-Path $repo 'artifacts/t121-install-home'
    $info.Environment['MUTHUR_URL'] = 'http://127.0.0.1:1'
    $info.Environment['MUTHUR_AGENT'] = ''; $info.Environment['MUTHUR_TOKEN'] = ''
    $process = [Diagnostics.Process]::Start($info)
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    $deadline=[DateTime]::UtcNow.AddMinutes(20); $heartbeat=[DateTime]::UtcNow.AddMinutes(3)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Isolated install exceeded 20 minutes' }
        if ([DateTime]::UtcNow -ge $heartbeat) {
            & muthur agent heartbeat --summary 'T-121: isolated AOT publish remains active.'
            $heartbeat=[DateTime]::UtcNow.AddMinutes(3)
        }
    }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath $log
    $stderr.GetAwaiter().GetResult() | Add-Content -LiteralPath $log
    $code=$process.ExitCode
    & muthur utility summarize --file $log --task T-121
    exit $code
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(10000) }
    if ($process) { $process.Dispose() }
}
