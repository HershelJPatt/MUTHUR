<#
.SYNOPSIS
  Removes the hub data directories a test run left behind, keeping only the ones whose muthur.log recorded
  an error -- T-41's root cause was read off exactly such a log, and nothing else records an unhandled
  server error. Directories written to within -OlderThanMinutes are left alone, because a dozen agents share
  this root and a half-deleted hub is worse than a leaked one. -All overrides the retention rule, not the
  age guard: someone reaching for it wants the logs gone, not to race a live test run.

  Every run ends with its counts, whatever happened to any one directory, and names every directory it held
  back or failed on. Losing the count is the defect this script exists to fix; it does not get to repeat it.
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
#
# Three answers, not two, and the read never throws. FileLoggerProvider holds muthur.log open with
# FileShare.ReadWrite; [IO.File]::ReadLines asks for FileShare.Read, which that writer's own write access
# refuses. So a log we cannot read means another process still owns this directory -- the one fact that
# should stop us touching it -- and that is a different fact from a log that recorded an error, so it gets
# its own verdict and its own counter. (TestHubDirectories reads with the wider share mode on purpose: it
# reads its own hub's log after disposal, where a refusal would lose exactly the evidence worth keeping.)
function Test-LoggedAnError([string]$LogPath) {
    try {
        foreach ($line in [IO.File]::ReadLines($LogPath)) {
            $tokens = $line.Split([char[]]@(), 3, [StringSplitOptions]::RemoveEmptyEntries)
            if ($tokens.Length -ge 2 -and ($tokens[1] -ceq 'Error' -or $tokens[1] -ceq 'Critical')) {
                return [pscustomobject]@{ Verdict = 'error'; Note = '' }
            }
        }
        return [pscustomobject]@{ Verdict = 'clean'; Note = '' }
    }
    catch {
        return [pscustomobject]@{ Verdict = 'held'; Note = $_.Exception.GetBaseException().Message }
    }
}

# The newest write anywhere under the directory, or $null when the owning run removed it while we looked.
# A directory's own LastWriteTimeUtc moves when an entry is created, renamed or removed -- measured: 200
# appends to an existing muthur.log left it untouched to the tick. A hub that has gone quiet while still
# holding its log would therefore look ancient, which is the case the age guard exists to prevent, so the
# guard reads the files too. A hub directory holds a handful of them; this is one cheap stat each.
function Get-NewestWriteUtc([string]$Dir) {
    try {
        $newest = [IO.Directory]::GetLastWriteTimeUtc($Dir)
        foreach ($file in [IO.Directory]::EnumerateFiles($Dir, '*', [IO.SearchOption]::AllDirectories)) {
            $written = [IO.File]::GetLastWriteTimeUtc($file)
            if ($written -gt $newest) { $newest = $written }
        }
        return $newest
    }
    catch {
        return $null
    }
}

$cutoff = [datetime]::UtcNow.AddMinutes(-$OlderThanMinutes)
$examined = 0
$deleted = 0
$kept = 0
$heldCount = 0
$skipped = 0
$gone = 0
$failed = 0
$heldLines = [System.Collections.Generic.List[string]]::new()
$failedLines = [System.Collections.Generic.List[string]]::new()
try {
    foreach ($dir in [IO.Directory]::EnumerateDirectories($Root)) {
        $examined++
        # There were 49,437 of these the first time this ran. A line each is noise; a line per thousand is progress.
        if ($examined % 1000 -eq 0) { Write-Host "  examined $examined..." }

        # Why a cleanup script cares what time it is: this root is shared, and Remove-Item -Recurse deletes
        # files as it walks. Against a hub another agent's test is still running it would take muthur.db and
        # the -wal with it before the locked muthur.log stopped it, leaving that run a corrupted database and
        # us one counted failure.
        $newest = Get-NewestWriteUtc $dir
        # EnumerateDirectories handed us a path the owning run has since removed. That is the routine case on
        # a shared root, not something an operator acts on, so it never counts as a failure.
        if ($null -eq $newest) {
            $gone++
            continue
        }
        if ($OlderThanMinutes -gt 0 -and $newest -gt $cutoff) {
            $skipped++
            continue
        }

        $log = Join-Path $dir 'muthur.log'
        # A directory with no log has nothing to preserve, which is also why muthur-cli-tests empties completely.
        if (-not $All -and [IO.File]::Exists($log)) {
            $answer = Test-LoggedAnError $log
            if ($answer.Verdict -ceq 'error') {
                $kept++
                continue
            }
            if ($answer.Verdict -ceq 'held') {
                $heldCount++
                $heldLines.Add("  held    $dir  ($($answer.Note))")
                continue
            }
        }
        if ($DryRun) {
            $deleted++
            continue
        }
        try {
            # -LiteralPath: a root with a bracket in it would otherwise be a wildcard, and this cmdlet deletes.
            Remove-Item -LiteralPath $dir -Recurse -Force
            $deleted++
        }
        catch {
            # The owning run removed it, or is removing it as we walk: ItemNotFoundException for a directory
            # that was already gone, a not-found for a file our own recursive delete expected to still be
            # there. Neither is something an operator acts on, and failed is the number they act on.
            $problem = $_.Exception.GetBaseException()
            $vanished = -not [IO.Directory]::Exists($dir) -or
                $problem -is [IO.FileNotFoundException] -or
                $problem -is [IO.DirectoryNotFoundException] -or
                $problem -is [System.Management.Automation.ItemNotFoundException]
            if ($vanished) {
                $gone++
            }
            else {
                # A handle somebody holds. Named, so it can be chased.
                $failed++
                $failedLines.Add("  failed  $dir  ($($problem.Message))")
            }
        }
    }
}
finally {
    # The count is the point of the script, so it survives whatever happened above it.
    Write-Host "${Root}: examined $examined, deleted $deleted, kept $kept, held $heldCount, skipped (too recent) $skipped, gone $gone, failed $failed."
    foreach ($line in $heldLines) { Write-Host $line }
    foreach ($line in $failedLines) { Write-Host $line }
    if ($DryRun) { Write-Host 'Dry run: nothing was deleted.' }
}
