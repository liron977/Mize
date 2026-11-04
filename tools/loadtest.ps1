# Concurrent load test against the running compose stack (http://localhost:8080).
# Usage: powershell -File tools\loadtest.ps1 [-BaseUrl http://localhost:8080]
# Respects the 100 req/min/supplier limit by spreading load over many suppliers.
param([string]$BaseUrl = 'http://localhost:8080')

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::DefaultConnectionLimit = 400
$http = [System.Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(60)
$url = "$BaseUrl/api/reservations/ingest"

$worker = {
  param($http,$url,$mode,$run,$w,$suppliers,$perSupplier)
  function Body($sup,$id){ "{`"supplierId`":`"$sup`",`"reservationId`":`"$id`",`"roomId`":`"room1`",`"checkIn`":`"2026-08-01T00:00:00Z`",`"checkOut`":`"2026-08-03T00:00:00Z`",`"price`":450.00,`"updatedAtUtc`":`"2026-07-01T10:15:00Z`"}" }
  function Send($b){
    $sw=[Diagnostics.Stopwatch]::StartNew()
    $c=[System.Net.Http.StringContent]::new($b,[Text.Encoding]::UTF8,'application/json')
    $r=$http.PostAsync($url,$c).GetAwaiter().GetResult(); $null=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop(); [pscustomobject]@{ Ms=$sw.Elapsed.TotalMilliseconds; Code=[int]$r.StatusCode }
  }
  $out = New-Object System.Collections.ArrayList
  foreach($s in 1..$suppliers){
    $sup = "$run-$mode-w$w-s$s"
    if($mode -eq 'dup'){
      $null = Send (Body $sup 'r')                                              # 1 create (not measured)
      foreach($i in 1..$perSupplier){ [void]$out.Add((Send (Body $sup 'r'))) }  # identical resends (measured)
    } else {
      foreach($i in 1..$perSupplier){ [void]$out.Add((Send (Body "$sup-$i" 'r'))) }  # unique supplier+reservation
    }
  }
  $out
}

function RunLoad($mode,$workers,$suppliers,$perSupplier){
  $run = "c$([guid]::NewGuid().ToString('N').Substring(0,6))"
  $pool = [runspacefactory]::CreateRunspacePool(1,$workers); $pool.Open()
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $jobs = 1..$workers | ForEach-Object {
    $ps=[powershell]::Create(); $ps.RunspacePool=$pool
    [void]$ps.AddScript($worker).AddArgument($http).AddArgument($url).AddArgument($mode).AddArgument($run).AddArgument($_).AddArgument($suppliers).AddArgument($perSupplier)
    [pscustomobject]@{ PS=$ps; H=$ps.BeginInvoke() } }
  $rows = $jobs | ForEach-Object { $_.PS.EndInvoke($_.H); $_.PS.Dispose() }
  $sw.Stop(); $pool.Close()
  $ms = $rows | ForEach-Object Ms | Sort-Object; $n=$ms.Count
  $codes = ($rows | Group-Object Code | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '

  # counter exactness: every dup supplier must show exactly 1 ingested + $perSupplier unchanged
  $check = ''
  if($mode -eq 'dup'){
    $bad = 0
    foreach($w in 1..$workers){ foreach($s in 1..$suppliers){
      $st = (Invoke-RestMethod "$BaseUrl/api/reservations/stats/$run-$mode-w$w-s$s")
      if($st.ingested -ne 1 -or $st.unchanged -ne $perSupplier){ $bad++ } } }
    $check = "  stats-exact: " + $(if($bad -eq 0){'OK'}else{"$bad MISMATCHES"})
  }
  "{0,-5} workers={1,-3} n={2,-5} wall={3,6:N1}s  rps={4,6:N0}  p50={5,7:N1}  p95={6,7:N1}  p99={7,7:N1}  max={8,8:N1} ms  codes: {9}{10}" -f $mode,$workers,$n,$sw.Elapsed.TotalSeconds,($n/$sw.Elapsed.TotalSeconds),$ms[[int]($n*0.5)],$ms[[int]($n*0.95)],$ms[[math]::Min($n-1,[int]($n*0.99))],$ms[$n-1],$codes,$check
}

$null = RunLoad 'uniq' 4 1 20   # warm-up (JIT, connections)
RunLoad 'dup'  1  20 19         # sequential-ish: 1 worker (latency at no concurrency)
RunLoad 'uniq' 1  20 19
RunLoad 'dup'  20 5 19
RunLoad 'uniq' 20 5 19
RunLoad 'dup'  50 3 19
RunLoad 'uniq' 50 3 19
