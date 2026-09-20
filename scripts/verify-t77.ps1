# Run against an installed CLI; no model sessions are started.
param([Parameter(Mandatory)][string]$Cli)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$serverName = if ($IsWindows) { 'Muthur.Server.exe' } else { 'Muthur.Server' }
$server = (Resolve-Path -LiteralPath (Join-Path (Split-Path $Cli) "server/$serverName")).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $tempRoot ('t77-' + [Guid]::NewGuid().ToString('N'))
$scratchHome = Join-Path $scratch 'home'
$kit = Join-Path $scratch 'kit'
$evidence = Join-Path $PSScriptRoot ('../artifacts/t77-verification-' + [Guid]::NewGuid().ToString('N'))
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$url = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
$listener.Stop()
$hub = $null
$hubOut = $null
$hubErr = $null
$checks = [Collections.Generic.List[string]]::new()

function Put([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $checks.Add($Message)
}
function StartChild([string]$Binary, [string[]]$Arguments) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Binary)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $scratch
    foreach ($name in 'MUTHUR_AGENT','MUTHUR_TOKEN','Muthur__KitDir','MUTHUR_SERVER') { $psi.Environment.Remove($name) | Out-Null }
    $psi.Environment['MUTHUR_HOME'] = $scratchHome
    $psi.Environment['MUTHUR_URL'] = $url
    $psi.Environment['MUTHUR_KIT'] = $kit
    foreach ($arg in $Arguments) { $psi.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Could not start $Binary" }
    return $process
}
function RunCli([string]$Label, [string[]]$Arguments, [int[]]$Allowed = @(0)) {
    $process = StartChild $Cli $Arguments
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw "Could not stop timed-out CLI: $Label" }
            throw "CLI timed out: $Label"
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        Put (Join-Path $evidence "$Label.stdout.json") $out
        Put (Join-Path $evidence "$Label.stderr.txt") $err
        if ($process.ExitCode -notin $Allowed) { throw "$Label exited $($process.ExitCode): $err" }
        return $out
    }
    finally { $process.Dispose() }
}
function StartHub {
    $script:hub = StartChild $server @()
    $script:hubOut = $hub.StandardOutput.ReadToEndAsync()
    $script:hubErr = $hub.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($hub.HasExited) { throw 'Scratch hub exited during startup.' }
        try {
            $response = Invoke-WebRequest "$url/api/v1/status" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Scratch hub did not become ready within 30 seconds.'
}
function StopHub([string]$Label) {
    if ($null -eq $script:hub) { return }
    try {
        if (-not $hub.HasExited) {
            try { $null = RunCli "$Label-down" @('down', '--founder') }
            finally {
                if (-not $hub.WaitForExit(10000)) { $hub.Kill($true) }
            }
        }
        if (-not $hub.WaitForExit(10000)) { throw 'Owned scratch hub did not stop.' }
        Put (Join-Path $evidence "$Label-hub.stdout.txt") $hubOut.GetAwaiter().GetResult()
        Put (Join-Path $evidence "$Label-hub.stderr.txt") $hubErr.GetAwaiter().GetResult()
        $checks.Add("$Label owned hub stopped")
    }
    finally { $hub.Dispose(); $script:hub = $null }
}
function Doctor([string]$Label) {
    # Other scratch-hub checks may fail; still require a valid doctor response.
    return (RunCli $Label @('doctor', '--offline', '--founder') @(0,1) | ConvertFrom-Json)
}
function Catalog([bool]$Ghost) {
    $candidates = @(@{ harness = 'good'; model = 'one' })
    if ($Ghost) { $candidates += @(@{ harness = 'ghost'; model = 'one'; account = 'a' }, @{ harness = 'ghost'; model = 'two'; account = 'b' }) }
    Put (Join-Path $scratchHome 'harnesses.json') (@{ tiers = @{ implementer = $candidates; mastermind = $candidates } } | ConvertTo-Json -Depth 8)
}

try {
    Put (Join-Path $kit 'good/kit.json') '{"files":[]}'
    Catalog $true
    StartHub
    $agents = RunCli 'zero-agents' @('agent', 'list', '--founder') | ConvertFrom-Json
    Check (@($agents.agents).Count -eq 0 -and $agents.conductorHidden -eq 0) 'Initial hub has zero agents'
    $report = Doctor 'catalog-only'
    $ghost = @($report.checks | Where-Object { $_.category -ceq 'harness' -and $_.subject -ceq 'ghost' })
    Check ($ghost.Count -eq 1 -and $ghost[0].status -ceq 'warn') 'Repeated ghost candidates produce exactly one harness warning'
    Check (@($report.checks | Where-Object subject -CEQ 'good').Count -eq 0) 'Known good kit produces no row'
    $expected = "Harness 'ghost' has a tier entry in harnesses.json but no kit, so a session started on it gets no procedures. Add kit/ghost/kit.json, or correct the spelling in harnesses.json."
    Check ($ghost[0].detail -ceq $expected) 'Catalog warning detail matches the frozen specification'
    $html = [Net.WebUtility]::HtmlDecode((Invoke-WebRequest "$url/operations" -TimeoutSec 15).Content)
    Check ($html.Contains($expected)) 'Operations prerender contains the catalog warning'
    $null = RunCli 'register' @('agent', 'register', '--name', 'standing', '--harness', 'ghost', '--model', 'one', '--founder')
    $report = Doctor 'standing'
    Check (@($report.checks | Where-Object { $_.category -ceq 'harness' -and $_.subject -ceq 'ghost' }).Count -eq 0) 'Standing ghost suppresses the catalog row'
    $agent = @($report.checks | Where-Object { $_.category -ceq 'agent' -and $_.subject -ceq 'standing' })
    Check ($agent.Count -eq 1 -and $agent[0].status -ceq 'warn' -and $agent[0].detail.Contains('a tier entry in harnesses.json but no kit')) 'Original agent warning remains'
    Catalog $false
    $report = Doctor 'catalog-removed'
    Check (@($report.checks | Where-Object { $_.category -ceq 'harness' -and $_.subject -ceq 'ghost' }).Count -eq 0) 'Removed catalog entry leaves no stale catalog row'
    StopHub 'first'
    $kit = Join-Path $scratch 'missing-kit'
    Catalog $true
    StartHub
    $report = Doctor 'missing-kit'
    Check (@($report.checks | Where-Object { $_.category -ceq 'harness' -and $_.subject -cne 'kit' }).Count -eq 0) 'Missing kit produces no standalone catalog rows'
}
finally {
    try { StopHub 'final' }
    finally {
        $resolved = [IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^t77-[0-9a-f]{32}$') { throw "Refusing cleanup outside unique temp root: $resolved" }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Check (-not (Test-Path -LiteralPath $resolved)) 'Scratch files removed'
        Put (Join-Path $evidence 'checks.txt') ($checks -join "`n")
    }
}
Write-Output "$($checks.Count) checks passed. Evidence: $evidence"
