<#
.SYNOPSIS
  Manually checks the per-supplier rate limit (default 100 requests per rolling 60 s) against a running stack.

.DESCRIPTION
  1. Sends N requests (default 105) for ONE fresh supplier, with distinct reservation ids, and counts the status codes.
     Expected: 201 x100, 429 x5.
  2. Sends one more request right away for the same supplier: still 429, and shows its Retry-After.
  3. Sends one request for a DIFFERENT supplier: 201, proving limits are isolated per supplier.
  4. Reads the stats endpoint: ingested / throttled counts must match what was observed.

  A fresh supplier id is generated every run, so old quota never interferes. Run it within the window; the quota is a rolling
  60 seconds, so waiting that long makes requests succeed again (that is correct behaviour, not a bug).

.EXAMPLE
  .\tools\throttle-test.ps1
  .\tools\throttle-test.ps1 -BaseUrl http://localhost:8080 -Requests 105 -Limit 100
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [int]$Requests = 105,
    [int]$Limit = 100,
    [string]$Supplier = ("THROTTLE_" + [guid]::NewGuid().ToString("N").Substring(0, 6))
)

$ErrorActionPreference = "Stop"

function Send([string]$supplier, [string]$reservationId) {
    $body = @{
        supplierId = $supplier; reservationId = $reservationId; roomId = "R1"
        checkIn = "2026-08-01T00:00:00Z"; checkOut = "2026-08-03T00:00:00Z"
        price = 100.0; updatedAtUtc = "2026-07-01T10:15:00Z"
    } | ConvertTo-Json
    try {
        $r = Invoke-WebRequest "$BaseUrl/api/reservations/ingest" -Method Post -ContentType "application/json" `
            -Body $body -UseBasicParsing
        [pscustomobject]@{ Status = [int]$r.StatusCode; RetryAfter = $null }
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw "Cannot reach $BaseUrl : $($_.Exception.Message)" }
        $ra = $null
        try { $ra = $resp.Headers["Retry-After"] } catch { }                        # Windows PowerShell 5.1
        if ($null -eq $ra) { try { $ra = ($resp.Headers.GetValues("Retry-After") | Select-Object -First 1) } catch { } }   # PowerShell 7
        [pscustomobject]@{ Status = [int]$resp.StatusCode; RetryAfter = $ra }
    }
}

function Check([string]$what, [bool]$ok) {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed = $true }
}

$failed = $false
# a new set of reservation ids every run, so re-running with the same supplier sends NEW reservations (201) and not resends (200 "unchanged")
$run = [guid]::NewGuid().ToString("N").Substring(0, 6)
Write-Host "Supplier under test: $Supplier   ($BaseUrl)`n"

# stats are cumulative per supplier, so remember where they started and compare the CHANGE (the supplier may have been used before)
$before = $null
try { $before = Invoke-RestMethod "$BaseUrl/api/reservations/stats/$Supplier" } catch { }
$ingested0 = if ($before) { [long]$before.ingested } else { 0 }
$throttled0 = if ($before) { [long]$before.throttled } else { 0 }

Write-Host "1) $Requests requests for one supplier"
$results = 1..$Requests | ForEach-Object { Send $Supplier "RES-$run-$_" }
$created = @($results | Where-Object Status -eq 201).Count
$throttled = @($results | Where-Object Status -eq 429).Count
$results | Group-Object Status | Sort-Object Name | ForEach-Object { Write-Host "     $($_.Name) x$($_.Count)" }
$expectedOk = [math]::Min($Requests, $Limit)
Check "first $expectedOk accepted (201)" ($created -eq $expectedOk)
Check "the rest throttled (429)" ($throttled -eq [math]::Max(0, $Requests - $Limit))
Check "the first $expectedOk were the accepted ones (throttling starts after the limit)" `
    (@($results | Select-Object -First $expectedOk | Where-Object Status -ne 201).Count -eq 0)

Write-Host "`n2) one more request, same supplier, right away"
$again = Send $Supplier "RES-$run-AFTER"
Check "still throttled (429)" ($again.Status -eq 429)
Check "tells the caller when to retry (Retry-After: $($again.RetryAfter))" ($null -ne $again.RetryAfter)

Write-Host "`n3) a different supplier is unaffected"
$other = Send ($Supplier + "_OTHER") "RES-$run-1"
Check "other supplier accepted (201)" ($other.Status -eq 201)

Write-Host "`n4) stats for $Supplier"
$stats = Invoke-RestMethod "$BaseUrl/api/reservations/stats/$Supplier"
$stats | Format-List | Out-String | Write-Host
Check "ingested went up by $created" (($stats.ingested - $ingested0) -eq $created)
Check "throttled went up by $($throttled + 1)  (includes the extra request in step 2)" (($stats.throttled - $throttled0) -eq ($throttled + 1))

if ($failed) { Write-Host "`nSOME CHECKS FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed." -ForegroundColor Green
