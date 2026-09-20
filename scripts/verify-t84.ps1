# Unattended installed-binary verification. Only the unique scratch hub is contacted.
param([Parameter(Mandatory)][string]$CliPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$serverName = if ($IsWindows) { 'Muthur.Server.exe' } else { 'Muthur.Server' }
$server = (Resolve-Path -LiteralPath (Join-Path (Split-Path $CliPath) "server/$serverName")).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$scratch = Join-Path $tempRoot ('t84-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $scratch 'repo'
$outside = Join-Path $scratch 'outside'
$evidence = Join-Path $PSScriptRoot ('../artifacts/t84-evidence/installed-' + [guid]::NewGuid().ToString('N'))
$links = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[string]]::new()
$savedEnv = @{}
$hub = $null
$hubOut = $null
$hubErr = $null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$url = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
$listener.Stop()

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
    foreach ($arg in $Arguments) { $psi.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Could not start $Binary" }
    return $process
}
function Run([string]$Binary, [string[]]$Arguments, [string]$Label, [int]$Expected = 0) {
    $process = StartChild $Binary $Arguments
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw "Cannot stop $Label" }
            throw "$Label timed out"
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        Put (Join-Path $evidence "$Label.stdout.txt") $out
        Put (Join-Path $evidence "$Label.stderr.txt") $err
        Check ($process.ExitCode -eq $Expected) "$Label exit $Expected (actual $($process.ExitCode))"
        return [pscustomobject]@{ Out = $out; Err = $err }
    }
    finally { $process.Dispose() }
}
function Link([string]$Path, [string]$Target) {
    $links.Add($Path)
    if ($IsWindows) {
        $null = Run 'cmd.exe' @('/d', '/c', 'mklink', '/J', $Path, $Target) "junction-$($links.Count)"
    }
    else { $null = [IO.Directory]::CreateSymbolicLink($Path, $Target) }
}
function Api([string]$Method, [string]$Path, [object]$Body, [string]$Token) {
    $parameters = @{
        Uri = "$url/api/v1/$Path"; Method = $Method; TimeoutSec = 10
        Headers = @{ Authorization = "Bearer $Token" }
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }
    return Invoke-RestMethod @parameters
}
function OptionalProperty([object]$Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Attach([string]$Id, [string]$Path, [string]$Label, [bool]$Accept) {
    $before = Api 'GET' "tasks/$Id" $null $agentToken
    $expected = if ($Accept) { 0 } else { 2 }
    $result = Run $CliPath @('task', 'spec', $Id, $Path, '--as-agent', 't84-owner') $Label $expected
    $after = Api 'GET' "tasks/$Id" $null $agentToken
    if ($Accept) {
        $task = $result.Out | ConvertFrom-Json
        Check ($task.specPath -ceq $Path -and (OptionalProperty $after.task 'specPath') -ceq $Path) "$Label attaches path"
        Check ($null -eq (OptionalProperty $after.task 'attendedReason')) "$Label stays unattended"
    }
    else {
        $errorBody = $result.Err | ConvertFrom-Json
        Check ($errorBody.code -ceq 'spec_outside_repository') "$Label stable refusal code"
        Check ($errorBody.message -ceq 'The spec path must stay inside the project repository.') "$Label refusal message"
        Check ((OptionalProperty $before.task 'specPath') -ceq (OptionalProperty $after.task 'specPath')) "$Label preserves attachment"
        Check ((OptionalProperty $before.task 'attendedReason') -ceq (OptionalProperty $after.task 'attendedReason')) "$Label preserves attended reason"
        Check ($before.task.state -ceq $after.task.state) "$Label preserves task state"
        Check (($before.events | ConvertTo-Json -Depth 20 -Compress) -ceq ($after.events | ConvertTo-Json -Depth 20 -Compress)) "$Label preserves ledger events"
    }
}

try {
    # Restore every changed variable, including unset ones, even after startup or fixture failure.
    $names = @('MUTHUR_HOME','MUTHUR_URL','MUTHUR_AGENT','MUTHUR_TOKEN','Muthur__BackgroundServices') +
        @(Get-ChildItem Env: | Where-Object Name -Match '^MUTHUR_|^Muthur__' | Select-Object -ExpandProperty Name)
    foreach ($name in $names | Select-Object -Unique) {
        $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    $env:MUTHUR_HOME = Join-Path $scratch 'home'
    $env:MUTHUR_URL = $url
    $env:Muthur__BackgroundServices = 'false'
    Put (Join-Path $repo 'docs/spec.md') "# Frozen spec`n"
    Put (Join-Path $outside 'nested/spec.md') "# Frozen spec`nneeds: browser`n"
    Put (Join-Path $outside 'spec.md') "# Frozen spec`nneeds: browser`n"
    $null = Run 'git' @('-C', $repo, 'init', '-q', '-b', 'main') 'git-init'
    $null = Run 'git' @('-C', $repo, '-c', 'user.name=T84', '-c', 'user.email=t84@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-q', '-m', 'scratch') 'git-commit'
    $hub = StartChild $server @()
    $hubOut = $hub.StandardOutput.ReadToEndAsync()
    $hubErr = $hub.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $ready = $false
    do {
        if ($hub.HasExited) { throw 'Scratch hub exited during startup.' }
        try {
            $status = Invoke-WebRequest "$url/api/v1/status" -TimeoutSec 2
            $ready = $status.StatusCode -eq 200
        }
        catch [System.Net.Http.HttpRequestException] { }
        catch [System.Threading.Tasks.TaskCanceledException] { }
        # Each probe observes readiness; no sleep or fixed delay decides when to proceed.
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    Check $ready 'Scratch hub ready within 30 seconds'
    $founderToken = [IO.File]::ReadAllText((Join-Path $env:MUTHUR_HOME 'founder.token')).Trim()
    $null = Run $CliPath @('agent', 'register', '--name', 't84-owner', '--harness', 'local', '--model', 'none', '--founder') 'register'
    $agentToken = [IO.File]::ReadAllText((Join-Path $env:MUTHUR_HOME 'agents/t84-owner.token')).Trim()
    $null = Api 'POST' 'projects' @{ key = 'plain'; repoPath = $repo; requiredValidators = @(); ingestSources = @() } $founderToken
    $task = Api 'POST' 'tasks' @{ title = 'Containment'; project = 'plain' } $agentToken
    $null = Api 'POST' "tasks/$($task.id)/claim" @{} $agentToken
    # Reject before any attachment to prove outside needs: browser cannot flag a fresh task.
    Link (Join-Path $repo 'out') $outside
    Attach $task.id 'out/spec.md' 'outward-fresh' $false
    Attach $task.id 'docs/spec.md' 'plain' $true
    Attach $task.id 'out/spec.md' 'outward-existing' $false
    Link (Join-Path $repo 'inward') (Join-Path $repo 'docs')
    Attach $task.id 'inward/spec.md' 'inward' $true
    Link (Join-Path $repo 'bridge') $outside
    Link (Join-Path $repo 'entry') (Join-Path $repo 'bridge/nested')
    Attach $task.id 'entry/spec.md' 'target-ancestor' $false
    $via = Join-Path $scratch 'via'
    Link $via $repo
    $null = Api 'POST' 'projects' @{ key = 'linked'; repoPath = $via; requiredValidators = @(); ingestSources = @() } $founderToken
    $linkedTask = Api 'POST' 'tasks' @{ title = 'Linked root'; project = 'linked' } $agentToken
    $null = Api 'POST' "tasks/$($linkedTask.id)/claim" @{} $agentToken
    Attach $linkedTask.id 'docs/spec.md' 'linked-root' $true
    Attach $linkedTask.id 'out/spec.md' 'linked-root-outward' $false
}
finally {
    try {
        if ($null -ne $hub) {
            try {
                if (-not $hub.HasExited) {
                    try { $null = Run $CliPath @('down', '--founder') 'hub-down' }
                    finally {
                        if (-not $hub.WaitForExit(10000)) { $hub.Kill($true) }
                    }
                }
                if (-not $hub.WaitForExit(10000)) { throw 'Owned scratch hub did not stop.' }
                Put (Join-Path $evidence 'hub.stdout.txt') $hubOut.GetAwaiter().GetResult()
                Put (Join-Path $evidence 'hub.stderr.txt') $hubErr.GetAwaiter().GetResult()
                Check $hub.HasExited 'Owned scratch hub stopped'
            }
            finally { $hub.Dispose() }
        }
    }
    finally {
        try {
            # Validate the owned absolute root, unlink explicitly, then recursively remove it.
            $resolved = [IO.Path]::GetFullPath($scratch)
            if (-not $resolved.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolved) -notmatch '^t84-[0-9a-f]{32}$') { throw "Refusing cleanup: $resolved" }
            for ($i = $links.Count - 1; $i -ge 0; $i--) {
                try { [IO.Directory]::Delete($links[$i]) }
                catch [IO.DirectoryNotFoundException] { }
            }
            if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
            Check (-not (Test-Path -LiteralPath $resolved)) 'Owned scratch data removed'
        }
        finally {
            foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
            Put (Join-Path $evidence 'checks.txt') ($checks -join "`n")
        }
    }
}
Write-Output "$($checks.Count) checks passed. Evidence: $evidence"
