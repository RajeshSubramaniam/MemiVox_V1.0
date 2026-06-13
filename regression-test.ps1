## MyScorer Full Regression Test Script
## Run: pwsh -File regression-test.ps1
#Requires -Version 7

$base = "http://localhost:5201"
$adminH = @{"X-Admin-Password"="admin@myscorer2026"}
$setupH = @{"X-Setup-Password"="1234"}
$pass = 0; $fail = 0; $total = 0

function Test($label, $condition) {
    $script:total++
    if ($condition) { $script:pass++; Write-Host "[PASS] $label" }
    else { $script:fail++; Write-Host "[FAIL] $label" }
}

function WebReq($url, [hashtable]$headers=$null, [string]$method="GET", [string]$body=$null) {
    $params = @{ Uri=$url; SkipHttpErrorCheck=$true; Method=$method }
    if ($headers) { $params.Headers = $headers }
    if ($body) { $params.ContentType = "application/json"; $params.Body = $body }
    Invoke-WebRequest @params
}

Write-Host "`n========================================"
Write-Host "  MYSCORER FULL REGRESSION TEST"
Write-Host "========================================`n"

# ── 1. HEALTH ──
Write-Host "── 1. HEALTH ENDPOINTS ──"
$r = Invoke-RestMethod "$base/health"
Test "/health -> healthy" ($r.status -eq "healthy" -and $r.memoryMB -gt 0)
$r = Invoke-RestMethod "$base/health/detail"
Test "/health/detail -> commands + maintenance" ($null -ne $r.commands -and $null -ne $r.maintenance)
Test "/health/detail -> threadPoolThreads" ($r.threadPoolThreads -gt 0)
Test "/health/detail -> scoreCacheCount present" ($null -ne $r.scoreCacheCount)

# ── 2. ROUTE SECURITY ──
Write-Host "`n── 2. ROUTE SECURITY ──"
$routeTests = @(
    @("/23082201", 200, "Setup page"),
    @("/cheesecake/admin", 200, "Admin portal"),
    @("/23082201/live", 200, "Overlay page"),
    @("/admin", 404, "/admin blocked"),
    @("/setup", 404, "/setup blocked"),
    @("/overlay", 404, "/overlay blocked"),
    @("/cheesecake", 404, "/cheesecake blocked"),
    @("/favicon.ico", 404, "/favicon.ico blocked"),
    @("/23082201/sd/sdsd", 404, "Deep path 404")
)
foreach ($t in $routeTests) {
    $r = WebReq "$base$($t[0])"
    Test "$($t[2]) -> $($r.StatusCode)" ($r.StatusCode -eq $t[1])
}

# ── 3. AUTH ──
Write-Host "`n── 3. AUTHENTICATION ──"
# Setup auth
$r = WebReq "$base/api/setup/23082201/matches"
Test "Setup: no auth -> 401" ($r.StatusCode -eq 401)

$r = WebReq "$base/api/setup/23082201/matches" -headers @{"X-Setup-Password"="wrong"}
Test "Setup: wrong pwd -> 401" ($r.StatusCode -eq 401)

$r = WebReq "$base/api/setup/23082201/matches" -headers $setupH
Test "Setup: correct pwd -> 200" ($r.StatusCode -eq 200)

$r = WebReq "$base/api/setup/23082201/matches" -headers $adminH
Test "Setup: admin bypass -> 200" ($r.StatusCode -eq 200)

$r = WebReq "$base/api/setup/23082201/live-score"
Test "Live-score: public -> 200" ($r.StatusCode -eq 200)

$r = WebReq "$base/api/setup/23082201/active-match"
Test "Active-match: no auth -> 401" ($r.StatusCode -eq 401)

# Admin auth
$r = WebReq "$base/api/admin/setups"
Test "Admin: no auth -> 401" ($r.StatusCode -eq 401)

$r = WebReq "$base/api/admin/setups" -headers @{"X-Admin-Password"="bad"}
Test "Admin: wrong pwd -> 401" ($r.StatusCode -eq 401)

$r = WebReq "$base/api/admin/setups" -headers $adminH
Test "Admin: correct pwd -> 200" ($r.StatusCode -eq 200)

# Validate admin password (no auth required)
$r = WebReq "$base/api/admin/validate-password" -method Post -body '{"password":"admin@myscorer2026"}'
Test "Admin validate-password correct -> 200" ($r.StatusCode -eq 200)
$parsed = $r.Content | ConvertFrom-Json
Test "Admin validate-password -> valid=true" ($parsed.valid -eq $true)

$r = WebReq "$base/api/admin/validate-password" -method Post -body '{"password":"wrong"}'
$parsed = $r.Content | ConvertFrom-Json
Test "Admin validate-password wrong -> valid=false" ($parsed.valid -eq $false)

# ── 4. ADMIN CRUD ──
Write-Host "`n── 4. ADMIN CRUD ──"
# List setups
$setups = Invoke-RestMethod "$base/api/admin/setups" -Headers $adminH
Test "List setups -> count > 0" ($setups.Count -gt 0)

# List clients
$clients = Invoke-RestMethod "$base/api/admin/clients" -Headers $adminH
Test "List clients -> count > 0" ($clients.Count -gt 0)

# List matches for setup (route: /api/admin/matches/{setupId})
$matches = Invoke-RestMethod "$base/api/admin/matches/23082201" -Headers $adminH
Test "List matches for 23082201 -> count > 0" ($matches.Count -gt 0)

# Register new setup (POST /api/admin/setups)
$newSetup = @{
    ownerName = "Regression Test"
    ownerEmail = "regression@test.com"
    startDate = "2026-06-13T00:00:00"
    cameraSerialNumber = "CAM-REG"
    yoloSerialNumber = "YLB-REG"
    powerBankSerialNumber = "PWR-REG"
    status = "Active"
    password = "testpwd123"
} | ConvertTo-Json
$r = WebReq "$base/api/admin/setups" -method Post -body $newSetup -headers $adminH
Test "Register new setup -> 200" ($r.StatusCode -eq 200)
$newSetupData = $r.Content | ConvertFrom-Json
$newSetupId = $newSetupData.setupId
Write-Host "    (created SetupId: $newSetupId)"

# Verify new setup appears in list
$setups2 = Invoke-RestMethod "$base/api/admin/setups" -Headers $adminH
Test "New setup in list" ($setups2.Count -gt $setups.Count)

# Validate new client password
$r = WebReq "$base/api/setup/$newSetupId/validate-password" -method Post -body '{"password":"testpwd123"}'
$parsed = $r.Content | ConvertFrom-Json
Test "New client pwd validates" ($parsed.valid -eq $true)

# Update client (POST /api/admin/clients/{setupId})
$r = WebReq "$base/api/admin/clients/$newSetupId" -method Post -body '{"emailId":"updated@test.com"}' -headers $adminH
Test "Update client email -> 200" ($r.StatusCode -eq 200)

# Search setups
$r = Invoke-RestMethod "$base/api/admin/setups?query=Regression" -Headers $adminH
Test "Search setups by name" ($r.Count -gt 0)

# Search clients
$r = Invoke-RestMethod "$base/api/admin/clients?query=updated@test.com" -Headers $adminH
Test "Search clients by email" ($r.Count -gt 0)

# CricHeroes BuildId
$r = WebReq "$base/api/admin/cricheroes-buildid" -headers $adminH
Test "Get CricHeroes buildId -> 200" ($r.StatusCode -eq 200)

# ── 5. SETUP API ──
Write-Host "`n── 5. SETUP API ──"
# Validate password
$r = WebReq "$base/api/setup/23082201/validate-password" -method Post -body '{"password":"1234"}'
$parsed = $r.Content | ConvertFrom-Json
Test "Setup validate correct pwd -> valid=true" ($parsed.valid -eq $true)

$r = WebReq "$base/api/setup/23082201/validate-password" -method Post -body '{"password":"wrong"}'
$parsed = $r.Content | ConvertFrom-Json
Test "Setup validate wrong pwd -> valid=false" ($parsed.valid -eq $false)

# Get matches
$r = WebReq "$base/api/setup/23082201/matches" -headers $setupH
Test "Setup get matches -> 200" ($r.StatusCode -eq 200)

# Get active match
$r = WebReq "$base/api/setup/23082201/active-match" -headers $setupH
Test "Setup active-match -> 200 or 204" ($r.StatusCode -eq 200 -or $r.StatusCode -eq 204)

# Live score (public)
$r = Invoke-RestMethod "$base/api/setup/23082201/live-score"
Test "Live-score -> has setupId" ($r.setupId -eq "23082201")

# Input validation
$r = WebReq "$base/api/setup/a@b/matches" -headers $setupH
Test "Bad setupId -> 400" ($r.StatusCode -eq 400)

# ── 6. DEVICE LIFECYCLE ──
Write-Host "`n── 6. DEVICE LIFECYCLE ──"
# Use unique device ID for clean test
$devId = "pi-reg-$(Get-Random -Maximum 9999)"

# Heartbeat - new device
$hb = @{deviceId=$devId; name="Regression-Pi"; deviceType="raspberry-pi"; atemConnected=$true; streamActive=$false; networkStatus="connected"} | ConvertTo-Json
$r = Invoke-RestMethod "$base/api/device/heartbeat" -Method Post -ContentType "application/json" -Body $hb
Test "Heartbeat new device -> received" ($r.received -eq $true)

# Status check
$r = Invoke-RestMethod "$base/api/device/$devId/status"
Test "Device status -> online" ($r.isOnline -eq $true -and $r.atemConnected -eq $true)

# Heartbeat update
$hb2 = @{deviceId=$devId; name="Regression-Pi-v2"; deviceType="raspberry-pi"; atemConnected=$false; streamActive=$true; networkStatus="connected"} | ConvertTo-Json
Invoke-RestMethod "$base/api/device/heartbeat" -Method Post -ContentType "application/json" -Body $hb2 | Out-Null
$r = Invoke-RestMethod "$base/api/device/$devId/status"
Test "Status update -> atem=false stream=true" ($r.atemConnected -eq $false -and $r.streamActive -eq $true)

# Device list
$r = Invoke-RestMethod "$base/api/device/devices"
Test "Device list -> count > 0" ($r.Count -gt 0)
$regDevice = $r | Where-Object { $_.deviceId -eq $devId }
Test "Regression device in list" ($null -ne $regDevice)

# Reset to streamActive=false so START_STREAM isn't skipped
$hb3 = @{deviceId=$devId; name="Regression-Pi"; deviceType="raspberry-pi"; atemConnected=$true; streamActive=$false; networkStatus="connected"} | ConvertTo-Json
Invoke-RestMethod "$base/api/device/heartbeat" -Method Post -ContentType "application/json" -Body $hb3 | Out-Null

# Queue START_STREAM
$r = Invoke-RestMethod "$base/api/device/command" -Method Post -ContentType "application/json" -Body "{`"deviceId`":`"$devId`",`"command`":`"START_STREAM`"}"
Test "Queue START_STREAM -> queued" ($r.queued -eq $true)
$cmdId = $r.commandId
Write-Host "    (commandId: $cmdId)"

# Duplicate blocked
$r = Invoke-RestMethod "$base/api/device/command" -Method Post -ContentType "application/json" -Body "{`"deviceId`":`"$devId`",`"command`":`"START_STREAM`"}"
Test "Duplicate command blocked" ($r.queued -eq $false)

# Poll
$r = Invoke-RestMethod "$base/api/device/$devId/next-command"
Test "Poll -> START_STREAM cmdId=$cmdId" ($r.hasCommand -eq $true -and $r.command -eq "START_STREAM" -and $r.commandId -eq $cmdId)

# Ack
$r = Invoke-RestMethod "$base/api/device/command/ack" -Method Post -ContentType "application/json" -Body "{`"commandId`":$cmdId,`"status`":`"Completed`"}"
Test "Ack -> Completed" ($r.status -eq "Completed")

# Empty queue
$r = Invoke-RestMethod "$base/api/device/$devId/next-command"
Test "Queue empty after ack" ($r.hasCommand -eq $false)

# Re-queue STOP after completion
$r = Invoke-RestMethod "$base/api/device/command" -Method Post -ContentType "application/json" -Body "{`"deviceId`":`"$devId`",`"command`":`"STOP_STREAM`"}"
Test "Re-queue STOP_STREAM -> queued" ($r.queued -eq $true)
$cmdId2 = $r.commandId

# Poll + Ack STOP
$r = Invoke-RestMethod "$base/api/device/$devId/next-command"
Test "Poll STOP_STREAM" ($r.hasCommand -eq $true -and $r.command -eq "STOP_STREAM")
Invoke-RestMethod "$base/api/device/command/ack" -Method Post -ContentType "application/json" -Body "{`"commandId`":$cmdId2,`"status`":`"Completed`"}" | Out-Null
$r = Invoke-RestMethod "$base/api/device/$devId/next-command"
Test "Queue empty after STOP ack" ($r.hasCommand -eq $false)

# ── 7. MATCH REGISTRATION ──
Write-Host "`n── 7. MATCH REGISTRATION ──"
# Complete any existing active match first
WebReq "$base/api/setup/23082201/complete-match" -method Post -headers $setupH | Out-Null

# Register match (POST /api/setup/{setupId}/matches)
$matchBody = @{matchUrl="https://www.playhq.com/cricket-australia/org/nsw-cricket/nsw-premier-cricket/season-2024-25/round/1/match/regtest123"; providerType="PlayHQ"} | ConvertTo-Json
$r = WebReq "$base/api/setup/23082201/matches" -method Post -body $matchBody -headers $setupH
Test "Register match -> 200" ($r.StatusCode -eq 200)

# Active match
$r = WebReq "$base/api/setup/23082201/active-match" -headers $setupH
Test "Active match present -> 200" ($r.StatusCode -eq 200)
if ($r.StatusCode -eq 200) {
    $active = $r.Content | ConvertFrom-Json
    Test "Active match URL correct" ($active.matchUrl -like "*regtest123*")
}

# List matches (should include the new one)
$r = Invoke-RestMethod "$base/api/setup/23082201/matches" -Headers $setupH
Test "Matches list includes new match" ($r.Count -gt 0)

# ── 8. MATCH STATE (in-memory) ──
Write-Host "`n── 8. MATCH STATE (in-memory) ──"
$r = Invoke-RestMethod "$base/api/match/23082201"
Test "Get match state -> has setupId" ($r.setupId -eq "23082201")

$updateBody = @{runs=250; wickets=7; overs="45.3"; teamA="TestTeamA"; teamB="TestTeamB"; status="Live"} | ConvertTo-Json
$r = Invoke-RestMethod "$base/api/match/23082201/update" -Method Post -ContentType "application/json" -Body $updateBody
Test "Update match -> 250/7 (45.3)" ($r.runs -eq 250 -and $r.wickets -eq 7 -and $r.overs -eq "45.3")

$r = Invoke-RestMethod "$base/api/match/23082201"
Test "Match state persisted" ($r.runs -eq 250 -and $r.teamA -eq "TestTeamA")

# ── 9. LIVE SCORE ──
Write-Host "`n── 9. LIVE SCORE ──"
$r = Invoke-RestMethod "$base/api/setup/23082201/live-score"
Test "Live-score response shape" ($null -ne $r.setupId -and $null -ne $r.matchUrl)

# ── 10. INPUT VALIDATION ──
Write-Host "`n── 10. INPUT VALIDATION ──"
$r = WebReq "$base/api/match/a@b"
Test "Match: special chars in ID -> 400" ($r.StatusCode -eq 400)

$r = WebReq "$base/api/match/abc..def"
Test "Match: dots in ID -> 400" ($r.StatusCode -eq 400)

$r = WebReq "$base/api/device/heartbeat" -method Post -body '{}'
Test "Heartbeat: empty body -> 400" ($r.StatusCode -eq 400)

$r = WebReq "$base/api/device/command" -method Post -body '{"deviceId":"","command":"START_STREAM"}'
Test "Command: empty deviceId -> 400" ($r.StatusCode -eq 400)

$r = WebReq "$base/api/device/command" -method Post -body '{"deviceId":"pi-test","command":"INVALID"}'
Test "Command: invalid command -> 400" ($r.StatusCode -eq 400)

# ── 11. RATE LIMITING ──
Write-Host "`n── 11. RATE LIMITING ──"
$rlSetupId = "23082202"
# Send 6 bad password attempts
for ($i = 1; $i -le 6; $i++) {
    $r = WebReq "$base/api/setup/$rlSetupId/validate-password" -method Post -body '{"password":"wrong"}'
}
Test "Rate limit after 6 bad attempts -> 429" ($r.StatusCode -eq 429)

# ── SUMMARY ──
Write-Host "`n========================================"
Write-Host "  REGRESSION RESULTS: $pass PASSED / $fail FAILED / $total TOTAL"
Write-Host "========================================"
if ($fail -gt 0) { Write-Host "  *** FAILURES DETECTED ***" -ForegroundColor Red }
else { Write-Host "  ALL TESTS PASSED" -ForegroundColor Green }
