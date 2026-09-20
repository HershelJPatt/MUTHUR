# Run against an installed CLI; the orchestrator runs this after installation.
param([Parameter(Mandatory)][string]$CliPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$serverName = if ($IsWindows) { 'Muthur.Server.exe' } else { 'Muthur.Server' }
$server = (Resolve-Path -LiteralPath (Join-Path (Split-Path $CliPath) "server/$serverName")).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = [IO.Path]::GetFullPath((Join-Path $tempRoot ('t78-' + [Guid]::NewGuid().ToString('N'))))
$scratchHome = Join-Path $scratch 'home'
$kit = Join-Path $scratch 'kit'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$url = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
$listener.Stop()
$hub = $null
$ready = $false

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}
function Put([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function StartChild([string]$Binary, [string[]]$Arguments) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Binary)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $scratch
    # Change only each child's environment: the invoking session needs no restoration.
    foreach ($name in @($psi.Environment.Keys)) {
        if ($name -match '^(MUTHUR_|Muthur__|ASPNETCORE_|DOTNET_URLS$)') {
            $psi.Environment.Remove($name) | Out-Null
        }
    }
    $psi.Environment['MUTHUR_HOME'] = $scratchHome
    $psi.Environment['MUTHUR_URL'] = $url
    $psi.Environment['MUTHUR_KIT'] = $kit
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw 'Could not start owned child process.' }
    return $process
}
function RunCli([string]$Label, [string[]]$Arguments, [int[]]$Allowed = @(0)) {
    $process = StartChild $CliPath $Arguments
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw "Could not stop owned CLI: $Label" }
            throw "CLI timed out: $Label"
        }
        # Do not print raw process output: even startup/error output may contain credentials.
        $null = $stderr.GetAwaiter().GetResult()
        Check ($process.ExitCode -in $Allowed) "$Label exit code $($process.ExitCode)"
        $script:lastCliExit = $process.ExitCode
        return $stdout.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}
function Doctor {
    $report = RunCli 'doctor' @('doctor', '--offline', '--founder') @(0, 1) | ConvertFrom-Json
    # Other checks can fail on a fresh hub, but the CLI must agree with the report.
    $expected = if ($report.fail -gt 0) { 1 } else { 0 }
    Check ($script:lastCliExit -eq $expected) 'Doctor exit code agrees with its failure count'
    return $report
}

try {
    Put (Join-Path $kit 'CustomHarness/kit.json') '{"files":[]}'
    Put (Join-Path $scratchHome 'harnesses.json') '{"tiers":{"mastermind":[{"harness":"CustomHarness","model":"test"}]}}'
    $help = (RunCli 'registration help' @('agent', 'register', '--help')) -replace '\s+', ' '
    Check ($help.Contains('Whitespace is trimmed; case is preserved. Match kit/catalog spelling exactly.')) 'Help explains harness casing'

    $hub = StartChild $server @()
    $hubOut = $hub.StandardOutput.ReadToEndAsync()
    $hubErr = $hub.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($hub.HasExited) { throw 'Owned scratch hub exited during startup.' }
        try {
            $status = Invoke-RestMethod "$url/api/v1/status" -TimeoutSec 2
            $ready = $status.processId -eq $hub.Id -and
                [IO.Path]::GetFullPath($status.dataDirectory) -eq $scratchHome -and
                (Test-Path -LiteralPath (Join-Path $scratchHome 'founder.token'))
        }
        catch { }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    Check ($ready -and -not $hub.HasExited) 'Owned scratch hub ready'

    $registered = RunCli 'mixed-case registration' @('agent', 'register', '--name', 'mixed', '--harness', '  CustomHarness  ', '--model', 'test', '--founder') | ConvertFrom-Json
    Check ($registered.harness -ceq 'CustomHarness') 'Registration trims whitespace and preserves case'
    $roster = RunCli 'roster' @('agent', 'list', '--founder') | ConvertFrom-Json
    Check (@($roster.agents).Count -eq 1 -and $roster.agents[0].harness -ceq 'CustomHarness') 'Roster preserves case'
    $report = Doctor
    $check = @($report.checks | Where-Object { $_.category -ceq 'agent' -and $_.subject -ceq 'mixed' })
    Check ($check.Count -eq 1 -and $check[0].status -ceq 'ok') 'Matching kit and catalog pass doctor'

    $lower = RunCli 'lowercase registration' @('agent', 'register', '--name', 'lower', '--harness', 'customharness', '--model', 'test', '--founder') | ConvertFrom-Json
    Check ($lower.harness -ceq 'customharness') 'Unknown lowercase identifier remains accepted'
    $report = Doctor
    $check = @($report.checks | Where-Object { $_.category -ceq 'agent' -and $_.subject -ceq 'lower' })
    Check ($check.Count -eq 1 -and $check[0].status -ceq 'fail') 'Case mismatch fails agent doctor check'
    $updated = RunCli 're-registration' @('agent', 'register', '--name', 'mixed', '--harness', 'customharness', '--model', 'test', '--founder') | ConvertFrom-Json
    Check ($updated.harness -ceq 'customharness') 'Re-registration updates casing'
    $roster = RunCli 'updated roster' @('agent', 'list', '--founder') | ConvertFrom-Json
    $mixed = @($roster.agents | Where-Object name -CEQ 'mixed')
    Check ($mixed.Count -eq 1 -and $mixed[0].harness -ceq 'customharness') 'Updated casing is stored'
}
finally {
    try {
        if ($null -ne $hub) {
            try {
                if (-not $hub.HasExited) {
                    try {
                        if ($ready) { $null = RunCli 'scratch shutdown' @('down', '--founder') }
                    }
                    finally {
                        if (-not $hub.WaitForExit(10000)) { $hub.Kill($true) }
                    }
                }
                if (-not $hub.WaitForExit(10000)) { throw 'Owned scratch hub did not stop.' }
                $null = $hubOut.GetAwaiter().GetResult()
                $null = $hubErr.GetAwaiter().GetResult()
            }
            finally { $hub.Dispose() }
        }
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $relative = [IO.Path]::GetRelativePath($tempRoot, $resolved)
        if ($relative -notmatch '^t78-[0-9a-f]{32}$' -or [IO.Path]::IsPathRooted($relative)) {
            throw "Refusing cleanup outside the unique scratch directory: $resolved"
        }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Check (-not (Test-Path -LiteralPath $resolved)) 'Scratch files removed; parent environment unchanged'
    }
}
