<#
.SYNOPSIS
  Touchline API health check. Hits the main public routes and, after signing in as a test
  user, the signed-in and admin routes, and reports what is UP, WARN, DOWN or SKIP.

.DESCRIPTION
  - It signs in through Supabase itself, so you never copy a token by hand.
  - The password is typed in hidden and never printed or saved. The token is never printed.
  - It needs the Supabase PUBLISHABLE key (sb_publishable_... or the legacy anon key), read from
    .env (SUPABASE_PUBLISHABLE_KEY) or typed at the prompt. It refuses a secret / service-role key.
  - For any route that fails with a 500, it prints the traceId to search for in the Railway logs.
  - Exit code 0 means nothing is DOWN; 1 means something is DOWN.
  - Runs on Windows PowerShell 5.1 and PowerShell 7+.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\api-health-check.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\api-health-check.ps1 -BaseUrl http://localhost:5147
.EXAMPLE
  pwsh -File ./scripts/api-health-check.ps1 -NoAuth
#>
param(
  [string]$BaseUrl = "https://touchline-api-production-ed283561.up.railway.app",
  [string]$SupabaseUrl = "https://exyzbdbitrsidompguda.supabase.co",
  [string]$PublishableKey,
  [string]$Email,
  [switch]$NoAuth,
  [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 (.NET Framework) may not offer TLS 1.2 by default.
if ($PSVersionTable.PSVersion.Major -lt 6) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
Add-Type -AssemblyName System.Net.Http
$BaseUrl = $BaseUrl.TrimEnd('/')
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$results = New-Object System.Collections.Generic.List[object]

# ---------- helpers ----------
# Reads a value from .env in the current folder, then from the repo root (the script's parent folder).
function Read-DotEnvValue([string[]]$names) {
  $files = @(".env")
  if ($PSScriptRoot) { $files += (Join-Path (Split-Path $PSScriptRoot -Parent) ".env") }
  foreach ($file in $files) {
    if (-not (Test-Path $file)) { continue }
    foreach ($line in Get-Content $file) {
      foreach ($n in $names) {
        if ($line -match ("^\s*" + [regex]::Escape($n) + "\s*=\s*(.*)$")) {
          $v = $matches[1].Trim().Trim('"').Trim("'")
          if ($v) { return $v }
        }
      }
    }
  }
  return $null
}

function Send-Request([string]$method, [string]$url, [hashtable]$headers, [string]$jsonBody) {
  $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
  if ($headers) { foreach ($k in $headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, [string]$headers[$k]) } }
  if ($jsonBody) { $req.Content = New-Object System.Net.Http.StringContent($jsonBody, [System.Text.Encoding]::UTF8, "application/json") }
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop()
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $body; Ms = $sw.ElapsedMilliseconds; Error = $null }
  } catch {
    $sw.Stop()
    $err = $_.Exception.GetBaseException()
    $msg = $err.Message
    if ($err -is [System.Threading.Tasks.TaskCanceledException] -or $err -is [System.TimeoutException]) { $msg = "Timed out after " + $TimeoutSeconds + " s" }
    return [pscustomobject]@{ Status = 0; Body = $null; Ms = $sw.ElapsedMilliseconds; Error = $msg }
  } finally {
    $req.Dispose()
  }
}

function ConvertFrom-JsonSafe($text) {
  if (-not $text) { return $null }
  try { return ($text | ConvertFrom-Json) } catch { return $null }
}

function Test-Prop($j, [string]$name) {
  return ($null -ne $j -and $j -isnot [System.Array] -and $null -ne $j.PSObject.Properties[$name])
}

# Item count of a list response: "count" when present, else the length of the named array.
function Get-ItemCount($j, [string]$arrayName) {
  if ($null -eq $j) { return $null }
  if ($j -is [System.Array]) { return $j.Count }
  if ($arrayName -and (Test-Prop $j $arrayName)) { return @($j.$arrayName).Count }
  if (Test-Prop $j "count") { return $j.count }
  return $null
}

function Format-Count($n, [string]$label) {
  if ($null -eq $n) { return "" }
  return ("" + $n + " " + $label)
}

# PowerShell 7 turns ISO dates into DateTime; 5.1 leaves strings. Handle both.
function ConvertTo-UtcDate($value) {
  if ($value -is [DateTime]) { return $value.ToUniversalTime() }
  return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

function Get-TraceHint($j) {
  if ((Test-Prop $j "traceId") -and $j.traceId) {
    $parts = ([string]$j.traceId) -split '-'
    if ($parts.Count -ge 2) { return " | Railway logs: search " + $parts[1] }
  }
  return ""
}

function Add-Result($area, $check, $state, $resp, $note) {
  $code = ""; $ms = ""
  if ($resp) { $code = $resp.Status; $ms = $resp.Ms }
  $results.Add([pscustomobject]@{ Area = $area; Check = $check; State = $state; Code = $code; Ms = $ms; Note = $note })
}

function Set-LastResult($state, $note) {
  $last = $results[$results.Count - 1]
  $last.State = $state
  $last.Note = $note
}

# Runs one GET check. $okCodes count as UP; $skipCodes count as SKIP; anything else is DOWN.
# $describe receives (json, resp) and returns a short note for the UP case.
function Test-Route($area, $check, $path, [int[]]$okCodes = @(200), [int[]]$skipCodes = @(), [hashtable]$headers = $null, [scriptblock]$describe = $null) {
  $r = Send-Request "GET" ($BaseUrl + $path) $headers $null
  $j = ConvertFrom-JsonSafe $r.Body
  if ($r.Status -eq 0) { Add-Result $area $check "DOWN" $r ("No response: " + $r.Error); return $null }
  if ($okCodes -contains $r.Status) {
    $note = ""
    if ($describe) { try { $note = [string](& $describe $j $r) } catch { $note = "(could not read the response: " + $_.Exception.Message + ")" } }
    Add-Result $area $check "UP" $r $note
    return $j
  }
  if ($skipCodes -contains $r.Status) {
    $skipNote = switch ($r.Status) { 403 { "Not an admin account" } default { "Skipped (status " + $r.Status + ")" } }
    Add-Result $area $check "SKIP" $r $skipNote
    return $null
  }
  $title = ""
  if (Test-Prop $j "title") { $title = [string]$j.title }
  $why = switch ($r.Status) {
    401 { "Rejected the token: check Supabase URL/audience in Railway" }
    403 { "Forbidden" }
    404 { "Route not found: is Railway on the latest main?" }
    429 { "Rate limited: wait a minute and rerun" }
    503 { "Service unavailable: database or data not ready" }
    default { if ($r.Status -ge 500) { "Server error" } else { "Unexpected status" } }
  }
  Add-Result $area $check "DOWN" $r ($why + $(if ($title) { " (" + $title + ")" } else { "" }) + (Get-TraceHint $j))
  return $null
}

# A protected route must refuse anonymous callers with 401.
function Test-Anonymous($check, $path) {
  $r = Send-Request "GET" ($BaseUrl + $path) $null $null
  if ($r.Status -eq 401) { Add-Result "Security" $check "UP" $r "Rejects anonymous callers" }
  elseif ($r.Status -eq 0) { Add-Result "Security" $check "DOWN" $r ("No response: " + $r.Error) }
  else { Add-Result "Security" $check "DOWN" $r ("Expected 401, got " + $r.Status) }
}

# ---------- public routes ----------
Write-Host ""
Write-Host "Touchline API health check -> $BaseUrl" -ForegroundColor Cyan
Write-Host ("Started " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") + " | PowerShell " + $PSVersionTable.PSVersion) -ForegroundColor DarkGray
Write-Host ""

# /health: 200 {status:"ok"} or 503 {status:"degraded"} when the database doesn't answer.
$health = Test-Route "Public" "Health" "/api/v1/health" @(200, 503) @() $null { param($j, $r) "status=" + $j.status }
if ($health -and [string]$health.status -ne "ok") {
  Set-LastResult "WARN" ("status=" + $health.status + " (the API is up but its database check failed; see /health/ready and the Railway logs)")
}

# /health/ready: 200 {ready:true} or 503 {ready:false}.
$ready = Test-Route "Public" "Health: ready" "/api/v1/health/ready" @(200, 503) @() $null { param($j, $r) "ready=" + $j.ready }
if ($ready -and -not $ready.ready) { Set-LastResult "WARN" "ready=False (the database did not answer)" }

# /health/freshness: always 200; "fresh" says whether football data arrived recently.
$fresh = Test-Route "Public" "Health: freshness" "/api/v1/health/freshness" @(200) @() $null {
  param($j, $r) "fresh=" + $j.fresh + ", data age " + $j.ageMinutes + " min, stalled live fixtures=" + $j.stalledLiveFixtures
}
if ($fresh -and -not $fresh.fresh) {
  $hours = ""
  if ($null -ne $fresh.ageMinutes) { $hours = ", last football data " + [math]::Round([double]$fresh.ageMinutes / 60, 1) + " h ago" }
  Set-LastResult "WARN" ("fresh=False" + $hours + ", stalled live fixtures=" + $fresh.stalledLiveFixtures + " (sync not delivering)")
}

$season = Test-Route "Public" "Current season" "/api/v1/seasons/current" @(200) @() $null { param($j, $r) $j.name + " (id " + $j.id + ")" }

[void](Test-Route "Public" "Teams" "/api/v1/teams" @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "teams") "clubs" })
$clubs = Test-Route "Public" "Clubs" "/api/v1/clubs" @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "clubs") "clubs" }
if ($clubs -and @($clubs.clubs).Count -gt 0) {
  $clubId = @($clubs.clubs)[0].id
  [void](Test-Route "Public" "Club detail" ("/api/v1/clubs/" + $clubId) @(200) @() $null { param($j, $r) "club " + $clubId })
} else {
  Add-Result "Public" "Club detail" "SKIP" $null "No club to test with"
}

$players = Test-Route "Public" "Players" "/api/v1/players" @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "players") "players" }
if ($players -and @($players.players).Count -gt 0) {
  $playerId = @($players.players)[0].id
  [void](Test-Route "Public" "Player detail" ("/api/v1/players/" + $playerId) @(200) @() $null { param($j, $r) "player " + $playerId })
} else {
  Add-Result "Public" "Player detail" "SKIP" $null "No player to test with"
}
[void](Test-Route "Public" "Player rankings" "/api/v1/player-rankings" @(200) @() $null { param($j, $r) "" })

$fixtures = Test-Route "Public" "Fixtures" "/api/v1/fixtures" @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "fixtures") "fixtures" }
$finishedId = $null
if ($fixtures -and $fixtures.fixtures) {
  $now = [DateTime]::UtcNow
  $overdue = @($fixtures.fixtures | Where-Object {
      $_.status -eq "Scheduled" -and $_.kickoffUtc -and ((ConvertTo-UtcDate $_.kickoffUtc) -lt $now.AddHours(-3))
    })
  $finished = @($fixtures.fixtures | Where-Object { $_.status -eq "Finished" })
  if ($finished.Count -gt 0) { $finishedId = $finished[$finished.Count - 1].id }
  if ($overdue.Count -gt 0) {
    Set-LastResult "WARN" ("" + $fixtures.count + " fixtures; " + $overdue.Count + " past kickoff but still 'Scheduled' (sync not updating)")
  }
}

if ($finishedId) {
  # Match detail: freshness.stale, revision and revisionSource ("worker" s{n} or "api_content_hash").
  [void](Test-Route "Public" "Match detail" ("/api/v1/fixtures/" + $finishedId) @(200) @() $null {
      param($j, $r)
      $s = "fixture " + $finishedId
      if (Test-Prop $j "freshness") { $s += ", stale=" + $j.freshness.stale }
      if (Test-Prop $j "revision") { $s += ", revision " + $j.revision + " (" + $j.revisionSource + ")" }
      $s
    })
  [void](Test-Route "Public" "Matches (web compat)" ("/api/v1/matches?fixtureId=" + $finishedId) @(200) @() $null { param($j, $r) "fixture " + $finishedId })
} else {
  Add-Result "Public" "Match detail" "SKIP" $null "No finished fixture to test with"
  Add-Result "Public" "Matches (web compat)" "SKIP" $null "No finished fixture to test with"
}

if ($season -and $season.id) {
  [void](Test-Route "Public" "Gameweeks" ("/api/v1/gameweeks?seasonId=" + $season.id) @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "gameweeks") "gameweeks" })
} else {
  Add-Result "Public" "Gameweeks" "SKIP" $null "No current season to test with"
}
[void](Test-Route "Public" "Standings" "/api/v1/standings" @(200) @() $null { param($j, $r) Format-Count (Get-ItemCount $j "standings") "rows" })
[void](Test-Route "Public" "Rules" "/api/v1/rules" @(200) @() $null { param($j, $r) if (Test-Prop $j "version") { "version " + $j.version } else { "" } })

# Security: protected routes must refuse anonymous callers.
Test-Anonymous "/me without token -> 401" "/api/v1/me"
Test-Anonymous "/fantasy/team no token -> 401" "/api/v1/fantasy/team"
Test-Anonymous "/admin/diag no token -> 401" "/api/v1/admin/diagnostics"

# ---------- sign in + signed-in routes ----------
if ($NoAuth) {
  Add-Result "Signed-in" "Signed-in and admin checks" "SKIP" $null "-NoAuth"
} else {
  if (-not $PSBoundParameters.ContainsKey("SupabaseUrl")) {
    $fromEnv = Read-DotEnvValue @("SUPABASE_URL", "Supabase__Url", "SupabaseAuth__Url", "Supabase__ProjectUrl")
    if ($fromEnv) { $SupabaseUrl = $fromEnv }
  }
  $SupabaseUrl = $SupabaseUrl.TrimEnd('/')
  if (-not $PublishableKey) { $PublishableKey = Read-DotEnvValue @("SUPABASE_PUBLISHABLE_KEY", "SUPABASE_ANON_KEY", "Supabase__PublishableKey", "Supabase__AnonKey") }
  if (-not $PublishableKey) { $PublishableKey = Read-Host "Supabase publishable key (sb_publishable_...)" }
  # Never use the secret key here: it bypasses row-level security and must stay on servers.
  $isSecret = $PublishableKey -like "sb_secret_*"
  if (-not $isSecret -and $PublishableKey -match '^eyJ[^.]+\.([^.]+)\.') {
    try {
      $payload = $matches[1].Replace('-', '+').Replace('_', '/')
      while ($payload.Length % 4) { $payload += "=" }
      $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
      if ($claims.role -eq "service_role") { $isSecret = $true }
    } catch { }
  }
  if ($isSecret) {
    Write-Host "That is a SECRET key (sb_secret_ / service_role). Use the publishable key (sb_publishable_... or the anon key) and rotate the secret key if it was shared." -ForegroundColor Red
    $http.Dispose()
    exit 2
  }
  if (-not $Email) { $Email = Read-Host "Test account email" }
  $secure = Read-Host ("Password for " + $Email) -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

  $loginBody = @{ email = $Email; password = $plain } | ConvertTo-Json -Compress
  $plain = $null
  $login = Send-Request "POST" ($SupabaseUrl + "/auth/v1/token?grant_type=password") @{ apikey = $PublishableKey } $loginBody
  $loginBody = $null
  $loginJson = ConvertFrom-JsonSafe $login.Body
  $login.Body = $null
  $token = $null
  if ($login.Status -eq 200 -and $loginJson -and $loginJson.access_token) {
    $token = [string]$loginJson.access_token
    Add-Result "Sign-in" "Supabase login" "UP" $login ("Signed in as " + $Email)
  } else {
    $msg = ""
    if ($loginJson) { foreach ($p in @("error_description", "msg", "message", "error")) { if ((Test-Prop $loginJson $p) -and $loginJson.$p) { $msg = [string]$loginJson.$p; break } } }
    if ($login.Status -eq 0) { $msg = $login.Error }
    Add-Result "Sign-in" "Supabase login" "DOWN" $login ("Login failed: " + $msg)
  }
  $loginJson = $null

  if ($token) {
    $auth = @{ Authorization = "Bearer " + $token }
    # /me: username, onboarding {needsUsername, needsTeam, needsSquad}, suspended.
    [void](Test-Route "Signed-in" "Me (profile)" "/api/v1/me" @(200) @() $auth {
        param($j, $r)
        $s = "username=" + $j.username
        if (Test-Prop $j "onboarding") {
          $o = $j.onboarding
          $s += " | needsUsername=" + $o.needsUsername + " needsTeam=" + $o.needsTeam + " needsSquad=" + $o.needsSquad
        }
        if ($j.suspended) { $s += " | SUSPENDED" }
        $s
      })
    [void](Test-Route "Signed-in" "Notification prefs" "/api/v1/me/notifications" @(200) @() $auth { param($j, $r) "" })
    $team = Test-Route "Signed-in" "Fantasy team" "/api/v1/fantasy/team" @(200, 404) @() $auth {
        param($j, $r)
        if ($r.Status -eq 404) { "No team on this account yet (normal)" } elseif (Test-Prop $j "name") { "team=" + $j.name } else { "" }
      }
    $hasTeam = ($null -ne $team -and (Test-Prop $team "name"))
    foreach ($route in @(
        @("Team snapshot", "/api/v1/fantasy/team/snapshot"),
        @("Squad", "/api/v1/fantasy/team/squad"),
        @("Points", "/api/v1/fantasy/team/points"),
        @("Chips", "/api/v1/fantasy/team/chips"),
        @("History", "/api/v1/fantasy/team/history"))) {
      if ($hasTeam) {
        [void](Test-Route "Signed-in" $route[0] $route[1] @(200) @() $auth { param($j, $r) "" })
      } else {
        [void](Test-Route "Signed-in" $route[0] $route[1] @(200, 404) @() $auth { param($j, $r) if ($r.Status -eq 404) { "No team yet (normal)" } else { "" } })
      }
    }
    [void](Test-Route "Signed-in" "My leagues" "/api/v1/fantasy/leagues" @(200) @() $auth { param($j, $r) Format-Count (Get-ItemCount $j "leagues") "leagues" })
    # Admin diagnostics: stalledLiveFixtures is {alert, count, source, fixtures}; 403 for non-admins.
    [void](Test-Route "Admin" "Admin diagnostics" "/api/v1/admin/diagnostics" @(200) @(403) $auth {
        param($j, $r)
        $st = $j.stalledLiveFixtures
        "stalled live fixtures=" + $st.count + " (alert=" + $st.alert + ", source " + $st.source + "), alerts=" + @($j.alerts).Count
      })
    $token = $null
    $auth = $null
  }
}

# ---------- report ----------
$colors = @{ UP = "Green"; WARN = "Yellow"; DOWN = "Red"; SKIP = "DarkGray" }
foreach ($r in $results) {
  $ms = ""
  if ("" -ne [string]$r.Ms) { $ms = [string]$r.Ms + "ms" }
  $line = "{0,-5} {1,-10} {2,-30} {3,4} {4,8}  {5}" -f $r.State, $r.Area, $r.Check, $r.Code, $ms, $r.Note
  Write-Host $line -ForegroundColor $colors[$r.State]
}

$up = @($results | Where-Object { $_.State -eq "UP" }).Count
$warn = @($results | Where-Object { $_.State -eq "WARN" }).Count
$down = @($results | Where-Object { $_.State -eq "DOWN" })
$skip = @($results | Where-Object { $_.State -eq "SKIP" }).Count

Write-Host ""
Write-Host ("Summary: {0} up, {1} warn, {2} down, {3} skipped" -f $up, $warn, $down.Count, $skip) -ForegroundColor Cyan
$http.Dispose()
if ($down.Count -gt 0) {
  Write-Host "What's down:" -ForegroundColor Red
  foreach ($d in $down) { Write-Host (" - " + $d.Area + " / " + $d.Check + ": " + $d.Note) -ForegroundColor Red }
  exit 1
}
Write-Host "Nothing is down." -ForegroundColor Green
exit 0
