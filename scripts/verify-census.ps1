# Installed-binary verification against a unique disposable hub; never contacts the live hub.
param([Parameter(Mandatory)][string]$InstallPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$InstallPath = (Resolve-Path -LiteralPath $InstallPath).Path
$cliName = if ($IsWindows) { 'muthur.exe' } else { 'muthur' }
$serverName = if ($IsWindows) { 'Muthur.Server.exe' } else { 'Muthur.Server' }
$cli = (Resolve-Path -LiteralPath (Join-Path $InstallPath $cliName)).Path
$server = (Resolve-Path -LiteralPath (Join-Path $InstallPath "server/$serverName")).Path
$pwsh = (Get-Command pwsh -CommandType Application).Source
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = [IO.Path]::GetFullPath((Join-Path $tempRoot ('census-' + [Guid]::NewGuid().ToString('N'))))
$savedEnv = @{}
$hub = $null
$hubOut = $null
$hubErr = $null
$checks = 0

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Put([string]$Path, [string]$Content) { [IO.File]::WriteAllText($Path, $Content) }
function StartChild([string]$Binary, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Binary)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $scratch
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw "Could not start $Binary" }
        return $child
    }
    catch { $child.Dispose(); throw }
}
function StopChild([Diagnostics.Process]$Child) {
    if (-not $Child.HasExited) {
        $Child.Kill($true)
        if (-not $Child.WaitForExit(10000)) { throw 'Scratch child did not stop within its cleanup deadline.' }
    }
}
function Cli([string[]]$Arguments, [switch]$Doctor) {
    $child = StartChild $cli $Arguments
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $all = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($child.WaitForExitAsync(), $stdout, $stderr))
        if (-not $all.Wait(45000)) { throw "CLI deadline exceeded: $($Arguments -join ' ')" }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if ($Doctor) {
            Check ($child.ExitCode -in 0, 1) "Unexpected doctor exit $($child.ExitCode): $err"
            $json = ConvertFrom-Json -InputObject $out -Depth 50
            $expected = if ($json.fail -gt 0) { 1 } else { 0 }
            Check ($child.ExitCode -eq $expected) 'Doctor exit does not match its JSON failure count.'
            return $json
        }
        Check ($child.ExitCode -eq 0) "CLI failed ($($Arguments -join ' ')), exit $($child.ExitCode): $err"
        return ConvertFrom-Json -InputObject $out -Depth 50
    }
    finally { StopChild $child; $child.Dispose() }
}
function Poll([int]$NewItems, [int]$Errors = 0) {
    $result = Cli @('inbound', 'poll', '--founder')
    Check ($result.sources -eq 1) 'Expected exactly one configured source.'
    Check ($result.newItems -eq $NewItems) "Expected $NewItems new inbound items."
    Check (@($result.errors).Count -eq $Errors) "Expected $Errors poll errors."
}
function Source {
    $sources = @(Cli @('inbound', 'sources'))
    Check ($sources.Count -eq 1) 'Expected one source record.'
    Check ($sources[0].source -ceq 'census:smoke') 'Source identity changed.'
    return $sources[0]
}
function Optional([object]$Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function IngestDoctor([string]$Status) {
    # This scratch project intentionally has no git history: evaluate the census row independently.
    $report = Cli @('doctor', '--offline') -Doctor
    $ingest = @($report.checks | Where-Object { $_.category -ceq 'ingest' })
    Check ($ingest.Count -eq 1 -and $ingest[0].subject -ceq 'census:smoke' -and $ingest[0].status -ceq $Status) "Expected census doctor status $Status."
}

try {
    $names = @('MUTHUR_HOME', 'MUTHUR_URL', 'MUTHUR_AGENT', 'MUTHUR_TOKEN', 'MUTHUR_KIT',
        'Muthur__BackgroundServices', 'Muthur__ConductorEnabled', 'Muthur__CensusChecks__smoke__FileName',
        'Muthur__CensusChecks__smoke__WorkingDirectory', 'Muthur__CensusChecks__smoke__TimeoutSeconds',
        'Muthur__CensusChecks__smoke__Arguments__0', 'Muthur__CensusChecks__smoke__Arguments__1',
        'Muthur__CensusChecks__smoke__Arguments__2', 'Muthur__CensusChecks__smoke__Arguments__3',
        'Muthur__CensusChecks__smoke__Arguments__4') +
        @(Get-ChildItem Env: | Where-Object Name -Match '^MUTHUR_|^Muthur__' | Select-Object -ExpandProperty Name)
    foreach ($name in $names | Select-Object -Unique) {
        $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $env:MUTHUR_HOME = Join-Path $scratch 'home'
    [IO.Directory]::CreateDirectory($env:MUTHUR_HOME) | Out-Null
    $repo = Join-Path $scratch 'repo'
    [IO.Directory]::CreateDirectory($repo) | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $env:MUTHUR_URL = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
    }
    finally { $listener.Stop() }
    $env:MUTHUR_KIT = Join-Path $InstallPath 'kit'
    $env:Muthur__BackgroundServices = 'false'
    $env:Muthur__ConductorEnabled = 'false'
    $fixture = Join-Path $scratch 'check.ps1'
    $findings = Join-Path $scratch 'findings.json'
    Put $fixture @'
param([string]$Findings)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$text = [IO.File]::ReadAllText($Findings)
if ($text -ceq 'nonzero') { [Console]::Out.Write('[]'); [Console]::Error.Write('PRIVATE-CENSUS-ERROR'); exit 17 }
[Console]::Out.Write($text)
'@
    $env:Muthur__CensusChecks__smoke__FileName = $pwsh
    $env:Muthur__CensusChecks__smoke__WorkingDirectory = $scratch
    $env:Muthur__CensusChecks__smoke__TimeoutSeconds = '10'
    $env:Muthur__CensusChecks__smoke__Arguments__0 = '-NoProfile'
    $env:Muthur__CensusChecks__smoke__Arguments__1 = '-NonInteractive'
    $env:Muthur__CensusChecks__smoke__Arguments__2 = '-File'
    $env:Muthur__CensusChecks__smoke__Arguments__3 = $fixture
    $env:Muthur__CensusChecks__smoke__Arguments__4 = $findings
    Put $findings '[{"id":"stable","title":"First observation","body":"Original"}]'
    $hub = StartChild $server @()
    $hubOut = $hub.StandardOutput.ReadToEndAsync()
    $hubErr = $hub.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $ready = $false
    do {
        if ($hub.HasExited) { throw 'Scratch hub exited during startup.' }
        try { $ready = (Invoke-WebRequest "$env:MUTHUR_URL/api/v1/status" -TimeoutSec 2).StatusCode -eq 200 }
        catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    Check $ready 'Scratch hub startup exceeded 30 seconds.'
    $project = Cli @('project', 'add', 'census-scratch', '--repo', $repo, '--ingest', 'census:smoke', '--founder')
    Check ($project.ingestSources[0] -ceq 'census:smoke') 'Registered source was not literal census:smoke.'
    Poll 1
    $items = @(Cli @('inbound', 'list', '--status', 'all'))
    Check ($items.Count -eq 1 -and $items[0].externalId -ceq 'smoke/stable/1') 'First incident identity is incorrect.'
    Check ($items[0].author -ceq 'census:smoke' -and $items[0].source -ceq 'census:smoke') 'Inbound source/author identity is incorrect.'
    $first = $items[0].id
    Poll 0
    Put $findings '[{"id":"stable","title":"Changed text","body":"Changed"}]'
    Poll 0
    $unchanged = Cli @('inbound', 'show', $first)
    Check ($unchanged.title -ceq 'First observation' -and $unchanged.body -ceq 'Original') 'Active text edit replaced inbound snapshot.'
    $null = Cli @('agent', 'register', '--name', 'census-intake', '--harness', 'local', '--model', 'none', '--founder')
    $converted = Cli @('inbound', 'claim', $first, '--as-task', '--as-agent', 'census-intake')
    Check ($converted.status -ceq 'converted') 'Claim/convert failed.'
    Poll 0
    $tasks = @(Cli @('task', 'list', '--all'))
    Check ($tasks.Count -eq 1 -and $tasks[0].id -ceq $converted.task) 'Repeated poll created another task.'
    Put $findings '[]'
    Poll 0
    Put $findings '[{"id":"stable","title":"Recurrence"}]'
    Poll 1
    $items = @(Cli @('inbound', 'list', '--status', 'all'))
    Check ($items.Count -eq 2 -and $items[1].externalId -ceq 'smoke/stable/2') 'Recurrence did not create a fresh incident.'
    $before = (Source).cursor
    foreach ($invalid in @('[{"id":"partial","title":"No partial write"},{"id":null}]', 'nonzero')) {
        Put $findings $invalid
        Poll 0 1
        $source = Source
        Check ($source.cursor -ceq $before) 'Failed poll advanced the cursor.'
        Check (-not [string]::IsNullOrWhiteSpace((Optional $source 'lastError'))) 'Failure did not set LastError.'
        Check ((Optional $source 'lastError') -notmatch 'PRIVATE|partial') 'Adapter exposed process output.'
        Check (@(Cli @('inbound', 'list', '--status', 'all')).Count -eq 2) 'Failure partially wrote inbound.'
        IngestDoctor 'fail'
    }
    Put $findings '[]'
    Poll 0
    Check ($null -eq (Optional (Source) 'lastError')) 'Recovery did not clear LastError.'
    IngestDoctor 'ok'
    $events = @(Cli @('log', '--limit', '200'))
    $completed = @($events | Where-Object { $_.type -ceq 'census.completed' })
    Check ($completed.Count -eq 7) 'Failed polls produced completion events, or successful polls lost them.'
    Check (@($completed | Where-Object { $_.payload.findings -eq 0 -and $_.payload.newItems -eq 0 -and $_.payload.source -ceq 'census:smoke' }).Count -eq 2) 'Missing zero-finding completion evidence.'
    Check (@($events | Where-Object { $_.type -ceq 'ingest.failing' }).Count -eq 1) 'Failure transition evidence is incorrect.'
    Check (@($events | Where-Object { $_.type -ceq 'ingest.recovered' }).Count -eq 1) 'Recovery transition evidence is incorrect.'
    $receipts = Cli @('receipts')
    Check ($receipts.workerRuns -eq 0 -and $receipts.conductorSessions -eq 0 -and $receipts.sessions -eq 1) 'A sweep launched work or registered an unexpected session.'
    Check ($receipts.byAccount.Count -eq 1 -and $receipts.byAccount[0].harness -ceq 'local' -and $receipts.byAccount[0].model -ceq 'none') 'Unexpected paid session identity.'
    Check (@(Cli @('agent', 'list', '--all')).Count -eq 1) 'Unexpected worker or session agent.'
    Write-Output "Census verification: PASS ($checks assertions)."
}
finally {
    try {
        if ($null -ne $hub) {
            try {
                StopChild $hub
                if ($null -ne $hubOut -and $null -ne $hubErr) {
                    $drains = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($hubOut, $hubErr))
                    if (-not $drains.Wait(10000)) { throw 'Scratch hub stream cleanup timed out.' }
                }
            }
            finally { $hub.Dispose() }
        }
    }
    finally {
        foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
        $resolved = [IO.Path]::GetFullPath($scratch)
        $relative = [IO.Path]::GetRelativePath($tempRoot, $resolved)
        if ($relative -notmatch '^census-[0-9a-f]{32}$' -or [IO.Path]::IsPathRooted($relative)) {
            throw "Refusing cleanup outside unique scratch directory: $resolved"
        }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
