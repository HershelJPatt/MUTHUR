<#
.SYNOPSIS
  Removes the hub data directories a test run left behind, keeping only the ones whose muthur.log recorded
  an error -- T-41's root cause was read off exactly such a log, and nothing else records an unhandled
  server error. Directories touched within -OlderThanMinutes are left alone, because a dozen agents share
  this root and a half-deleted hub is worse than a leaked one. -All overrides the retention rule, not the
  age guard: someone reaching for it wants the logs gone, not to race a live test run.
.EXAMPLE
  ./scripts/clean-test-temp.ps1 -DryRun    # count what would go; delete nothing
  ./scripts/clean-test-temp.ps1            # keep a directory only when its log holds an Error or Critical line
  ./scripts/clean-test-temp.ps1 -All       # delete every subdirectory old enough to be safe, logs and all
  ./scripts/clean-test-temp.ps1 -OlderThanMinutes 0   # no age guard, for a machine you know is quiet
  ./scripts/clean-test-temp.ps1 -Root (Join-Path $env:TEMP 'muthur-cli-tests')   # no logs there, so all of it goes
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'muthur-tests'),
    # Delete every subdirectory, including the ones whose log recorded an error.
    [switch]$All,
    # Report what a real run would do, and delete nothing.
    [switch]$DryRun,
    # Leave alone anything written to this recently, so a test run in progress is never touched. 0 disables it.
    [int]$OlderThanMinutes = 60
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Root)) {
    Write-Host "Nothing to clean: $Root does not exist."
    return
}

# The level is the second whitespace-separated token of a FileLogger line, which is written as
# "{timestamp} {level,-11} {category}: {message}". Read line by line and stop at the first match: a log can
# be megabytes and one Error line is the whole answer.
function Test-LoggedAnError([string]$LogPath) {
    foreach ($line in [IO.File]::ReadLines($LogPath)) {
        $tokens = $line.Split([char[]]@(), 3, [StringSplitOptions]::RemoveEmptyEntries)
        if ($tokens.Length -ge 2 -and ($tokens[1] -ceq 'Error' -or $tokens[1] -ceq 'Critical')) { return $true }
    }
    return $false
}

$cutoff = [datetime]::UtcNow.AddMinutes(-$OlderThanMinutes)
$examined = 0
$deleted = 0
$kept = 0
$skipped = 0
$failed = 0
foreach ($dir in [IO.Directory]::EnumerateDirectories($Root)) {
    $examined++
    # There were 49,437 of these the first time this ran. A line each is noise; a line per thousand is progress.
    if ($examined % 1000 -eq 0) { Write-Host "  examined $examined..." }

    # Why a cleanup script cares what time it is: this root is shared, and Remove-Item -Recurse deletes files
    # as it walks. Against a hub another agent's test is still running it would take muthur.db and the -wal
    # with it before the locked muthur.log stopped it, leaving that run a corrupted database and us one
    # counted failure. The directory's own timestamp is enough -- a live hub writes its log, and writing a
    # file touches the directory that holds it.
    if ($OlderThanMinutes -gt 0 -and [IO.Directory]::GetLastWriteTimeUtc($dir) -gt $cutoff) {
        $skipped++
        continue
    }

    $log = Join-Path $dir 'muthur.log'
    # A directory with no log has nothing to preserve, which is also why muthur-cli-tests empties completely.
    if (-not $All -and (Test-Path $log) -and (Test-LoggedAnError $log)) {
        $kept++
        continue
    }
    if ($DryRun) {
        $deleted++
        continue
    }
    try {
        Remove-Item $dir -Recurse -Force
        $deleted++
    }
    catch {
        # A handle still open on a log or a backup: report it and keep going, because the other 49,436 can go.
        $failed++
    }
}

Write-Host "${Root}: examined $examined, deleted $deleted, kept $kept, skipped (too recent) $skipped, failed $failed."
if ($DryRun) { Write-Host 'Dry run: nothing was deleted.' }
