param([string]$Output)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$script = Join-Path $root 'scripts/benchmark-workflow.ps1'
if (-not $Output) { $Output = Join-Path $root "artifacts/benchmark-cleanup-$([guid]::NewGuid().ToString('n'))" }
if (Test-Path -LiteralPath $Output) { throw 'Test output must be new.' }
New-Item -ItemType Directory -Path $Output | Out-Null
$checks = @()
foreach ($fault in @('after-scratch', 'during-child')) {
    $destination = Join-Path $Output $fault
    & pwsh -NoProfile -File $script -Baseline HEAD -Candidate HEAD -Output $destination -Fault $fault
    if ($LASTEXITCODE -eq 0) { throw "$fault incorrectly succeeded" }
    $report = Get-Content (Join-Path $destination 'comparison.json') -Raw | ConvertFrom-Json
    if ($report.status -ne 'incomplete/error' -or -not $report.cleanupComplete -or (Test-Path -LiteralPath $report.scratch)) { throw "$fault did not clean its owned scratch" }
    if (@($report.children | Where-Object { -not $_.exited }).Count -ne 0) { throw "$fault left a child alive" }
    if ($fault -eq 'during-child' -and @($report.children | Where-Object killed).Count -ne 1) { throw 'Cancellation did not terminate exactly its test child' }
    $checks += @{ name = $fault; passed = $true }
}
$destination = Join-Path $Output 'real'
& pwsh -NoProfile -File $script -Baseline HEAD -Candidate HEAD -Output $destination -Mode real
if ($LASTEXITCODE -eq 0) { throw 'Real mode incorrectly succeeded' }
$report = Get-Content (Join-Path $destination 'comparison.json') -Raw | ConvertFrom-Json
if ($report.status -ne 'unmeasured' -or @($report.children | Where-Object { $_.executable -ne 'git' }).Count -ne 0 -or $report.paidSessions -ne 0 -or $report.modelCalls -ne 0) { throw 'Real mode invoked work or claimed a measurement' }
$checks += @{ name = 'real-unmeasured'; passed = $true }
$destination = Join-Path $Output 'unknown'
& pwsh -NoProfile -File $script -Baseline HEAD -Candidate HEAD -Output $destination -Scenario unknown
if ($LASTEXITCODE -eq 0) { throw 'Unknown scenario incorrectly succeeded' }
$report = Get-Content (Join-Path $destination 'comparison.json') -Raw | ConvertFrom-Json
if ($report.status -ne 'incomplete/error') { throw 'Unknown scenario lacks explicit error' }
$checks += @{ name = 'unknown-scenario'; passed = $true }
$sentinel = Join-Path $destination 'sentinel.txt'
Set-Content -LiteralPath $sentinel -Value 'keep'
& pwsh -NoProfile -File $script -Baseline HEAD -Candidate HEAD -Output $destination 2> (Join-Path $Output 'existing-output.log')
if ($LASTEXITCODE -eq 0 -or (Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'keep') { throw 'Existing output was accepted or overwritten' }
$checks += @{ name = 'existing-output-preserved'; passed = $true }
$checks | ConvertTo-Json | Set-Content (Join-Path $Output 'checks.json')
Write-Output "Passed: $($checks.Count); Failed: 0. Evidence: $Output"
