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
    if (-not $IsWindows) {
        Write-Unavailable 'unsupported-platform: This runner requires Windows kill-on-close job ownership.'
        exit 2
    }
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
    # Compile the standalone owner without building the server or changing its source.
    if (-not ('Muthur.Server.Services.CensusWindowsProcess' -as [type])) {
        $source = Get-Content -Raw (Join-Path $PSScriptRoot '../src/Muthur.Server/Services/CensusWindowsProcess.cs')
        $source = "#nullable enable`nusing System;`nusing System.IO;`nusing System.Linq;`nusing System.Collections.Generic;`nusing System.Threading.Tasks;`n" +
            $source.Replace('internal sealed class CensusWindowsProcess', 'public sealed class CensusWindowsProcess')
        Add-Type -TypeDefinition $source
    }
    if ($deadline.ElapsedMilliseconds -ge $TimeoutSeconds * 1000) {
        Write-Unavailable 'launch-timeout: Total deadline exceeded during process-owner initialization.'
        exit 2
    }
    $arguments = [string[]] @((Join-Path $PSScriptRoot 'browser-capability.cjs'), ($options | ConvertTo-Json -Compress))
    $child = [Muthur.Server.Services.CensusWindowsProcess]::Start($selectedNode, $arguments, $PSScriptRoot)
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $remaining = [Math]::Max(0, $TimeoutSeconds * 1000 - [int] $deadline.ElapsedMilliseconds)
    $rootExited = $child.Process.WaitForExit($remaining)
    $remaining = [Math]::Max(0, $TimeoutSeconds * 1000 - [int] $deadline.ElapsedMilliseconds)
    if (-not $rootExited -or -not [System.Threading.Tasks.Task]::WaitAll(
            [System.Threading.Tasks.Task[]] @($stdout, $stderr), $remaining)) {
        Write-Unavailable 'launch-timeout: Total discovery/interaction/cleanup deadline exceeded; owned process tree terminated.'
        exit 2
    }
    $output = $stdout.GetAwaiter().GetResult()
    $diagnostic = $stderr.GetAwaiter().GetResult()
    if ($output.Trim()) { [Console]::Out.Write($output) }
    else { Write-Unavailable "unexpected-error: Node returned no probe JSON. $diagnostic" }
    if ($diagnostic.Trim()) { [Console]::Error.Write($diagnostic) }
    if (-not $output.Trim()) { exit 1 }
    exit $child.Process.ExitCode
}
catch {
    Write-Unavailable "unexpected-error: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($child) {
        # Closing the job kills descendants even when the root already exited with pipes open.
        $child.Dispose()
    }
}
