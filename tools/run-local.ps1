# Runs the API directly on Windows (no API container) with the SQLite file on YOUR disk, so Rider (or any SQLite tool) can open it.
#
#   powershell -File tools\run-local.ps1          # then Ctrl+C to stop
#
# What it does:
#   1. starts (or reuses) a Redis container published on localhost:6379 (the rate limiter needs a Redis)
#   2. runs the API on http://localhost:5080 with the database at <repo>\data\reservations.db (git-ignored)
#
# Why not just open the file from the docker compose stack? That database lives inside a Docker volume (in Docker's Linux VM),
# so it is not a file on your disk. Bind-mounting a Windows folder into the container is NOT a safe workaround: SQLite's WAL mode
# relies on shared memory and file locking that do not work reliably across the Windows/Linux boundary, and can hang or corrupt.
# Here the API and the file are both on Windows, which is fine.
#
# This is a separate instance from the compose stack: its own Redis, its own database, port 5080 (compose serves port 8080).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$db   = Join-Path $root 'data\reservations.db'

$existing = docker ps -a --format '{{.Names}}' | Where-Object { $_ -eq 'mize-redis-local' }
if ($existing) { docker start mize-redis-local | Out-Null }
else { docker run -d --name mize-redis-local -p 6379:6379 redis:7-alpine | Out-Null }   # dual-stack publish: localhost may resolve to ::1

$env:Sqlite__Path            = $db
$env:Redis__ConnectionString = 'localhost:6379'

Write-Host ''
Write-Host "API:       http://localhost:5080   (try: curl.exe -s http://localhost:5080/health)"
Write-Host "Database:  $db"
Write-Host "In Rider:  View > Tool Windows > Database > + > Data Source > SQLite > File = the path above (keep the -wal/-shm files next to it)"
Write-Host "Live view: python tools\show_db.py --supplier <id> --watch 2   reads the compose stack; for THIS file use Rider or sqlite3 on the path above"
Write-Host "Stop:      Ctrl+C here, then:  docker stop mize-redis-local"
Write-Host ''

dotnet run --project (Join-Path $root 'src\Mize.Reservations.Api') --no-launch-profile --urls http://localhost:5080
