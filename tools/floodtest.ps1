# Throttle-flood test against the running compose stack (http://localhost:8080).
# One noisy supplier is hammered far past its limit while calm suppliers send normal traffic.
# Checks: calm suppliers see no 429/503, and the noisy supplier's `throttled` stat equals the 429s the client received.
param([string]$BaseUrl = 'http://localhost:8080', [int]$FloodWorkers = 20, [int]$FloodPerWorker = 150,
      [int]$CalmSuppliers = 5, [int]$CalmPerSupplier = 40)

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::DefaultConnectionLimit = 400
$http = [System.Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(60)
$url = "$BaseUrl/api/reservations/ingest"
$run = "f$([guid]::NewGuid().ToString('N').Substring(0,6))"

$worker = {
  param($http,$url,$sup,$count,$sleepMs,$tag)
  function Body($s,$id){ "{`"supplierId`":`"$s`",`"reservationId`":`"$id`",`"roomId`":`"room1`",`"checkIn`":`"2026-08-01T00:00:00Z`",`"checkOut`":`"2026-08-03T00:00:00Z`",`"price`":450.00,`"updatedAtUtc`":`"2026-07-01T10:15:00Z`"}" }
  $out = New-Object System.Collections.ArrayList
  foreach($i in 1..$count){
    $sw=[Diagnostics.Stopwatch]::StartNew()
    $c=[System.Net.Http.StringContent]::new((Body $sup "$tag-$i"),[Text.Encoding]::UTF8,'application/json')
    $r=$http.PostAsync($url,$c).GetAwaiter().GetResult(); $null=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop(); [void]$out.Add([pscustomobject]@{ Ms=$sw.Elapsed.TotalMilliseconds; Code=[int]$r.StatusCode })
    if($sleepMs -gt 0){ Start-Sleep -Milliseconds $sleepMs }
  }
  $out
}

$noisy = "$run-noisy"
$pool = [runspacefactory]::CreateRunspacePool(1, $FloodWorkers + $CalmSuppliers); $pool.Open()
$sw = [Diagnostics.Stopwatch]::StartNew()
$flood = 1..$FloodWorkers | ForEach-Object { $ps=[powershell]::Create(); $ps.RunspacePool=$pool
  [void]$ps.AddScript($worker).AddArgument($http).AddArgument($url).AddArgument($noisy).AddArgument($FloodPerWorker).AddArgument(0).AddArgument("w$_")
  [pscustomobject]@{PS=$ps;H=$ps.BeginInvoke()} }
$calm = 1..$CalmSuppliers | ForEach-Object { $ps=[powershell]::Create(); $ps.RunspacePool=$pool
  [void]$ps.AddScript($worker).AddArgument($http).AddArgument($url).AddArgument("$run-calm$_").AddArgument($CalmPerSupplier).AddArgument(100).AddArgument("c")
  [pscustomobject]@{PS=$ps;H=$ps.BeginInvoke()} }

$calmRows  = $calm  | ForEach-Object { $_.PS.EndInvoke($_.H); $_.PS.Dispose() }
$floodRows = $flood | ForEach-Object { $_.PS.EndInvoke($_.H); $_.PS.Dispose() }
$sw.Stop(); $pool.Close()

function Pct($rows,$q){ $m=$rows|ForEach-Object Ms|Sort-Object; $m[[math]::Min($m.Count-1,[int]($m.Count*$q))] }
function Codes($rows){ ($rows | Group-Object Code | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ' }

"{0,-6} n={1,-5} p50={2,7:N1} p95={3,7:N1} p99={4,7:N1} ms   codes: {5}" -f 'calm',$calmRows.Count,(Pct $calmRows .5),(Pct $calmRows .95),(Pct $calmRows .99),(Codes $calmRows)
"{0,-6} n={1,-5} p50={2,7:N1} p95={3,7:N1} p99={4,7:N1} ms   codes: {5}   ({6:N0} req/s over {7:N1}s)" -f 'noisy',$floodRows.Count,(Pct $floodRows .5),(Pct $floodRows .95),(Pct $floodRows .99),(Codes $floodRows),($floodRows.Count/$sw.Elapsed.TotalSeconds),$sw.Elapsed.TotalSeconds

$st = Invoke-RestMethod "$BaseUrl/api/reservations/stats/$noisy"
$got201 = ($floodRows | Where-Object Code -eq 201).Count; $got429 = ($floodRows | Where-Object Code -eq 429).Count
"noisy stats: ingested=$($st.ingested) throttled=$($st.throttled)   client saw: 201x$got201 429x$got429"
$exact = ($st.ingested -eq $got201) -and ($st.throttled -eq $got429)
$calmOk = -not ($calmRows | Where-Object { $_.Code -ne 201 })
"counts exact (no lost/extra throttle counts): $(if($exact){'YES'}else{'NO'})"
"calm suppliers untouched (all 201):          $(if($calmOk){'YES'}else{'NO'})"
