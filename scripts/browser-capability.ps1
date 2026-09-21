#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $NodePath = 'node',
    [string] $PlaywrightPath,
    [string] $BrowserPath,
    [ValidateRange(1, 120)] [int] $TimeoutSeconds = 30,
    [string] $Url,
    [string] $AgentName
)

$ErrorActionPreference = 'Stop'
$deadline = [System.Diagnostics.Stopwatch]::StartNew()
$child = $null
$started = $false
$selectedNode = $null
function Write-Unavailable([string] $Reason) {
    @{
        connector = @{ status = 'unknown'; reason = 'Caller must inspect its connector.' }
        headless = @{ status = 'unavailable'; reason = $Reason; nodePath = $selectedNode;
            modulePath = $PlaywrightPath; browserPath = $BrowserPath }
        http = @{ status = 'render-only'; reason = 'HTTP is never interactive evidence.' }
    } | ConvertTo-Json -Depth 4 -Compress
}

try {
    $node = Get-Command -Name $NodePath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $node) {
        Write-Unavailable "node-absent: Cannot resolve NodePath '$NodePath'; supply an installed Node executable."
        exit 2
    }
    $selectedNode = $node.Source
    $options = @{ timeoutSeconds = $TimeoutSeconds }
    if ($PSBoundParameters.ContainsKey('PlaywrightPath')) { $options.playwrightPath = $PlaywrightPath }
    if ($PSBoundParameters.ContainsKey('BrowserPath')) { $options.browserPath = $BrowserPath }
    if ($PSBoundParameters.ContainsKey('Url')) { $options.url = $Url }
    if ($PSBoundParameters.ContainsKey('AgentName')) { $options.agentName = $AgentName }
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $selectedNode
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add((Join-Path $PSScriptRoot 'browser-capability.cjs'))
    $start.ArgumentList.Add(($options | ConvertTo-Json -Compress))
    $child = [System.Diagnostics.Process]::new()
    $child.StartInfo = $start
    if (-not $child.Start()) { throw 'Node process did not start.' }
    $started = $true
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $remaining = [Math]::Max(0, $TimeoutSeconds * 1000 - [int] $deadline.ElapsedMilliseconds)
    if (-not $child.WaitForExit($remaining)) {
        $child.Kill($true) # Only this invocation's child and descendants.
        $child.WaitForExit()
        Write-Unavailable 'launch-timeout: Total discovery/interaction/cleanup deadline exceeded; owned process tree terminated.'
        exit 2
    }
    $output = $stdout.GetAwaiter().GetResult()
    $diagnostic = $stderr.GetAwaiter().GetResult()
    if ($output.Trim()) { [Console]::Out.Write($output) }
    else { Write-Unavailable "unexpected-error: Node returned no probe JSON. $diagnostic" }
    if ($diagnostic.Trim()) { [Console]::Error.Write($diagnostic) }
    if (-not $output.Trim()) { exit 1 }
    exit $child.ExitCode
}
catch {
    Write-Unavailable "unexpected-error: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($child) {
        if ($started -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
}
