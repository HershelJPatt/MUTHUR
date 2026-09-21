$ErrorActionPreference = 'Stop'
$scratchArtifacts = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'
$env:MUTHUR_HOME = Join-Path $scratchArtifacts ('T-113-scratch-home-' + [guid]::NewGuid().ToString('N'))
$env:MUTHUR_URL = 'http://127.0.0.1:17413'
$env:MUTHUR_AGENT = 't113-smoke'
$env:MUTHUR_TOKEN = $null
$env:MUTHUR_SERVER = $null
$env:Muthur__ConductorEnabled = 'false'
$env:Muthur__ConductorOrchestrators = 'false'
$env:Muthur__ConductorMaxSessions = '2'
$env:Muthur__ConductorIntervalSeconds = '3600'
$cli = Join-Path $scratchArtifacts 'T-113-installed/muthur.exe'
function Invoke-Cli([string[]]$Arguments) {
    $out = & $cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Scratch CLI failed: $($Arguments -join ' '): $out" }
    if ($out) { $out | ConvertFrom-Json }
}
function Api([string]$Route, $Body, [int]$Expected = 200) {
    $reply = Invoke-WebRequest -Uri ($env:MUTHUR_URL + $Route) -Method Post -Headers $script:auth -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -SkipHttpErrorCheck
    if ([int]$reply.StatusCode -ne $Expected) { throw "Unexpected status $($reply.StatusCode): $($reply.Content)" }
    if ($reply.Content) { $reply.Content | ConvertFrom-Json }
}
try {
    Invoke-Cli @('up') | Out-Null
    Invoke-Cli @('agent','register','--name','t113-smoke','--harness','fixture','--model','fixture','--tier','mastermind') | Out-Null
    Invoke-Cli @('project','add','probe-smoke','--repo',(Split-Path $scratchArtifacts -Parent),'--branch','main','--founder') | Out-Null
    $task = Invoke-Cli @('task','add','T-113 scratch reservation fixture','--project','probe-smoke')
    Invoke-Cli @('task','claim',$task.id) | Out-Null
    '{"tiers":{"implementer":[{"harness":"fixture","model":"bounded","account":"fixture-account"}]}}' | Set-Content (Join-Path $env:MUTHUR_HOME 'harnesses.json')
    Invoke-Cli @('conductor','on','--founder') | Out-Null
    $script:auth = @{ Authorization = 'Bearer ' + (Get-Content (Join-Path $env:MUTHUR_HOME 'agents/t113-smoke.token') -Raw).Trim() }
    $request = @{task=$task.id;tier='implementer';harness='fixture';model='bounded';account='fixture-account';runId=[guid]::NewGuid().ToString('n')}
    $a = Api '/api/v1/workers/probes/admit' $request
    $again = Api '/api/v1/workers/probes/admit' $request
    if ($a.reservationId -ne $again.reservationId) { throw 'Admission not idempotent' }
    if (-not $a.mayExecute -or $again.mayExecute) { throw 'Admission replay granted execution' }
    $request.runId = [guid]::NewGuid().ToString('n')
    $b = Api '/api/v1/workers/probes/admit' $request
    $request.runId = [guid]::NewGuid().ToString('n')
    $denial = Api '/api/v1/workers/probes/admit' $request 422
    if ($denial.code -ne 'probe_capacity_exhausted') { throw "Wrong denial: $($denial.code)" }
    $status = Invoke-Cli @('conductor','status')
    if ($status.running -ne 2 -or $status.sessions.Count -ne 2) { throw 'Status capacity mismatch' }
    Api '/api/v1/workers/probes/release' @{reservationId=$a.reservationId} 204
    Api '/api/v1/workers/probes/release' @{reservationId=$a.reservationId} 204
    Invoke-Cli @('down') | Out-Null
    Invoke-Cli @('up') | Out-Null
    $restarted = Invoke-Cli @('conductor','status')
    if ($restarted.running -ne 1) { throw 'Restart lost the active reservation' }
    Api '/api/v1/workers/probes/release' @{reservationId=$b.reservationId} 204
    $status = Invoke-Cli @('conductor','status')
    if ($status.running -ne 0) { throw 'Reservation leak' }
    Invoke-Cli @('harness','limit','fixture-account','--minutes','5') | Out-Null
    $denial = Api '/api/v1/workers/probes/admit' $request 422
    if ($denial.code -ne 'probe_account_limited') { throw "Wrong account denial: $($denial.code)" }
    Invoke-Cli @('harness','limit','fixture-account','--clear') | Out-Null
    $c = Api '/api/v1/workers/probes/admit' $request
    Api '/api/v1/workers/probes/release' @{reservationId=$c.reservationId} 204
    $request.runId = [guid]::NewGuid().ToString('n')
    $denial = Api '/api/v1/workers/probes/admit' $request 422
    if ($denial.code -ne 'probe_budget_exhausted') { throw 'Daily budget was not preserved across release/restart' }
    [pscustomobject]@{result='PASS';task=$task.id;fixtureAdmissions=3;actualProbeStarts=0;remainingReservations=$status.running;url=$env:MUTHUR_URL} | ConvertTo-Json -Compress
}
finally {
    & $cli down
    if ($LASTEXITCODE -ne 0) { throw 'Scratch hub shutdown failed' }
}


