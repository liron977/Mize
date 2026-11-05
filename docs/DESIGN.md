# Reservation Ingest Service

An ASP.NET Core (.NET 8) Web API that sits in front of a reservations table. Suppliers push reservation updates through
a webhook-style feed; the service **de-duplicates** repeated updates and **throttles** suppliers that send too much.

- **Dedup/upsert:** the same reservation resent unchanged writes nothing and says so; changed details update the record.
- **Throttling:** more than 100 requests from one supplier in any rolling 60 seconds are rejected (429) until the window clears.
- **Retention (opt-in, off by default):** a separate worker archives reservations whose stay ended long ago, to a directory
  or to S3 / an S3-compatible server, and only then deletes them from the database (see [Data retention](#data-retention)).
- **Built to be swapped:** the business rules depend only on small ports (interfaces). Adapters: SQLite + Dapper for
  storage, Redis for the rate limiter, a directory or S3 for the archive; each can be replaced by configuration or by adding
  one project (see [Architecture](#architecture-and-extension-points)).

## Contents
[Quick start](#quick-start) · [API](#api) · [How a request is processed](#how-a-request-is-processed) ·
[Business rules](#business-rules) · [Rate limiting](#rate-limiting) · [Storage and group commit](#storage-and-group-commit) ·
[Observability](#observability) · [Failure behaviour](#failure-behaviour) · [Configuration](#configuration) · [Architecture](#architecture-and-extension-points) ·
[Testing](#testing) · [Alternatives considered](#alternatives-considered) · [Data retention](#data-retention) ·
[Limitations](#limitations) · [Performance](#performance-measurements) · [Layout](#repository-layout)

## Quick start

```
docker compose up --build        # nginx on http://localhost:8080 -> api-1, api-2 -> redis + shared SQLite volume
```

```
curl -X POST localhost:8080/api/reservations/ingest -H "Content-Type: application/json" -d '{
  "supplierId":"acme","reservationId":"r1","roomId":"room1",
  "checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z",
  "price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}'
curl localhost:8080/api/reservations/stats/acme
```

### Try the full flow and look at the data

With the stack running (`docker compose up --build -d`), this walks through every behaviour and prints what comes back
(Git Bash or WSL on Windows; plain PowerShell's `curl` is a different program):

```
bash tools/try-flow.sh               # new, duplicate, timestamp-only, update, stale, invalid payload, stats
bash tools/try-flow.sh --throttle    # plus 105 requests from one supplier: 100 accepted, then 429 with Retry-After
bash tools/try-flow.sh --malformed   # plus badly formatted payloads, each with its explanation (nothing is stored)
python tools/show_db.py --supplier demo-12345      # the rows and counters in the database, for the supplier it printed
python tools/show_db.py --supplier demo-12345 --watch 2   # LIVE view, refreshed every 2 s: keep it open in one terminal, run curls in another
```

The same steps as plain `curl` (use any supplier id; the service stores it trimmed and lower-cased):

```
S=acme   # a fresh id each time keeps the numbers predictable
post() { curl -s -w "  [HTTP %{http_code}]\n" -X POST localhost:8080/api/reservations/ingest -H "Content-Type: application/json" -d "$1"; }
body() { echo "{\"supplierId\":\"$S\",\"reservationId\":\"R1\",\"roomId\":\"room1\",\"checkIn\":\"2099-08-01T00:00:00Z\",\"checkOut\":\"2099-08-03T00:00:00Z\",\"price\":$1,\"updatedAtUtc\":\"$2\"}"; }

post "$(body 450.00 2026-07-01T10:15:00Z)"   # 201 {"status":"Created","changed":true}
post "$(body 450.00 2026-07-01T10:15:00Z)"   # 200 Unchanged   (exact duplicate: nothing is written)
post "$(body 450.00 2026-07-05T09:00:00Z)"   # 200 Unchanged   (same details, newer timestamp: only the timestamp moves)
post "$(body 400.00 2026-07-10T09:00:00Z)"   # 200 Updated     (price changed, newer: the row is updated in place)
post "$(body 999.00 2026-06-01T09:00:00Z)"   # 200 Stale       (older than what is stored: ignored)
curl -s localhost:8080/api/reservations/stats/$S   # {"ingested":2,"throttled":0,"unchanged":2,"stale":1}
```

`tools/show_db.py` reads the database that lives in the Docker volume (`/data/reservations.db`, WAL mode). The app image has no
`sqlite3` CLI, so the script copies the database files out of the container into a temp folder (including the `-wal` file so
recent writes are visible) and reads the copy with Python's built-in `sqlite3`; it never touches the live database. After the
walk-through above it shows one row for `R1` (price 400.00, the July 10 timestamp) and the stats row `ingested 2, unchanged 2,
stale 1`. To browse the file in a GUI such as DB Browser for SQLite, copy `reservations.db` and `reservations.db-wal` out with
`docker cp mize-api-1-1:/data/reservations.db .` (and the `-wal` file) and open the copy. Query it directly too:
`SELECT * FROM Reservations WHERE SupplierId = 'acme'` (prices are integer cents, times are UTC ISO-8601 text).
#### Open the database in Rider (or any SQLite tool)

The database of the compose stack lives inside a Docker volume (in Docker's Linux VM), so it is **not a file on your disk** and
Rider's Database tool cannot open it. Bind-mounting a Windows folder into the container is not a safe workaround: SQLite's WAL
mode relies on shared memory and file locking that do not work reliably across the Windows/Linux boundary, and can hang or
corrupt the database. Instead, run the API directly on Windows so the file is local:

```
powershell -File tools\run-local.ps1       # starts a Redis container + the API on http://localhost:5080; Ctrl+C to stop
```

Then in Rider: **View > Tool Windows > Database > + > Data Source > SQLite**, set **File** to `<repo>\data\reservations.db`, test
the connection (Rider offers to download the SQLite driver the first time) and open the `Reservations` and `SupplierStats` tables.
Keep the `-wal` and `-shm` files next to the `.db` file, and click Rider's refresh button to see new rows (the grid does not
auto-refresh). Send your curls to **port 5080** (the compose stack serves port 8080 and is a separate instance with its own
database and Redis). A read-only reader on that file sees live data and every new write immediately; this was verified with a
read-only SQLite connection to the file while the API was writing, not with Rider itself.
Running the API directly (it needs a Redis for the rate limiter; there is no in-process fallback in the product):

```
docker run -d --name ratelimit-redis -p 6379:6379 redis:7-alpine
dotnet run --project src/Mize.Reservations.Api      # uses localhost:6379 and data/reservations.db by default
```

The API listens on the URL printed at startup. `docker compose` runs two API instances behind nginx so the
multi-instance behaviour (shared limit, race-safe dedup) is exercised for real.

Retention is off unless you enable it. To archive and purge old reservations once (see [Data retention](#data-retention)):

```
RETENTION_ENABLED=true RETENTION_DAYS=365 docker compose --profile archive run --rm retention
# PowerShell: $env:RETENTION_ENABLED='true'; $env:RETENTION_DAYS='365'; docker compose --profile archive run --rm retention
```

Add `RETENTION_DRY_RUN=true` to only report what would happen. Set the same `RETENTION_ENABLED` / `RETENTION_DAYS` on the
API (`docker compose up`) so it ignores updates for stays that may already have been purged.

## API

`POST /api/reservations/ingest` with `supplierId, reservationId, roomId, checkIn, checkOut, price, updatedAtUtc`.

| Outcome | HTTP | Body |
|---|---|---|
| Created | 201 | `{"status":"Created","changed":true}` |
| Updated | 200 | `{"status":"Updated","changed":true}` |
| Unchanged (duplicate) | 200 | `{"status":"Unchanged","changed":false}` |
| Stale (older data) | 200 | `{"status":"Stale","changed":false}` |
| Expired (retention on; stay ended before the cutoff) | 200 | `{"status":"Expired","changed":false}` |
| Throttled | 429 | ProblemDetails + `Retry-After` (whole seconds, at least 1) |
| Invalid payload / malformed JSON | 400 | ProblemDetails with per-field errors |
| Rate limiter or storage unavailable | 503 | ProblemDetails + `Retry-After` |

Duplicates return 2xx on purpose: suppliers retry on non-2xx, so an error code would only create more retries.

`GET /api/reservations/stats/{supplierId}` returns `{ supplierId, ingested, throttled, unchanged, stale }`, where
`ingested` = created + updated. Unknown suppliers return zeros. `GET /health` is available for probes.

### Input format: what is accepted and what is rejected

The service reads the payload **strictly** and explains every problem instead of guessing. A rejected request stores nothing.

| Input | Accepted | Rejected, and the answer |
|---|---|---|
| Body | A JSON object, UTF-8 (a BOM is tolerated), at most 16 KB | Empty body: **400**. Not JSON: **400** with the line and position. JSON that is not an object (`[]`, `42`, `null`): **400**. Over 16 KB, even chunked: **413** |
| `Content-Type` | `application/json` (any case, with parameters such as `charset`) or `application/*+json` | Anything else, or none: **415** naming what is expected |
| `supplierId`, `reservationId`, `roomId` | A JSON **string**, trimmed, required, at most 100 / 200 / 100 characters, no control characters | A number, boolean, object or array is **rejected, not converted** (`"supplierId": 123` is a 400). Too long or with control characters: 400 |
| `checkIn`, `checkOut`, `updatedAtUtc` | A JSON string in ISO 8601 date-time form **with an explicit offset**: `2026-08-01T00:00:00Z`, `2026-08-01T02:00:00+02:00`, fractional seconds allowed. Years 1900 to 2200. **`checkOut` strictly after `checkIn`, compared as UTC instants**: `02:00+02:00` and `00:00Z` are the same moment, so that stay is rejected; a later instant that looks earlier on the clock is accepted | **No offset** (`2026-08-01T00:00:00`), a **date only**, an epoch number, another format, a lowercase `z`, or an impossible date: 400 with an example. Without an offset a timestamp would silently be read in the server's local time, which is wrong data that looks right |
| `price` | A JSON **number**, not negative, at most 2 decimals, at most 1,000,000,000 | The string `"450"`, a boolean or an array is **rejected, not coerced**; a number beyond decimal range; more than 2 decimals; negative: 400 |
| Property names, extra fields | Names are case-insensitive (`SupplierId` works); unknown fields are ignored so suppliers can add fields | Nothing |

Every problem is listed at once, in a standard `application/problem+json` body with an `errors` map, and no error body contains
a stack trace or exception text:

```
POST {"supplierId":123,"reservationId":"r1","roomId":"x","checkIn":"2026-08-01","checkOut":"2026-08-03T00:00:00Z","price":"450","updatedAtUtc":"2026-07-01T10:15:00Z"}
-> 400 {"title":"Invalid reservation payload.","status":400,"errors":{
     "supplierId":["supplierId must be a JSON string, not a number."],
     "checkIn":["checkIn must be a JSON string in ISO 8601 date-time format with an explicit UTC offset, for example \"2026-08-01T00:00:00Z\" ..."],
     "price":["price must be a JSON number such as 450.00, not a string."]}}
```

- A malformed request from a supplier whose id *can* be read still **costs rate-limit quota**, like any other request. A request whose
  supplier id cannot be read has no rate-limit key and is rejected without costing quota.
- A supplier id that is too long or has control characters is rejected **before** it can become a Redis key.
- Unreadable bodies are counted as `outcome="malformed"` in the metrics and logged as one warning per 30 seconds with a count of
  what was skipped; fields that were readable but invalid count as `invalid`.
- `GET /api/reservations/stats/{supplierId}` also answers 400 for a supplier id over 100 characters.
- Not covered: a limit on malformed requests from clients that send no readable supplier id at all (for example per IP address).
## How a request is processed

1. **Normalise the supplier** (trim, case-insensitive). A blank supplier is a 400.
2. **Rate limit**, before any other work. Every request that reaches the service costs quota, including invalid ones.
3. **If denied:** the `throttled` counter is incremented durably, then the caller gets 429 with `Retry-After`.
4. **Validate** the rest of the payload (400 with per-field errors on failure).
5. **Expired guard** (only when retention is enabled): a stay whose `checkOut` is before the retention cutoff is answered
   `Expired` (200) and never stored, because it may already have been archived and purged.
6. **Upsert** through the storage port. The business decision comes from `ReservationDecider`; the storage adapter
   applies it atomically, bumps the matching counter in the same transaction, and returns only after it is durable.
7. **Respond** with the outcome from the table above.

## Business rules

All of these live in `Core` (`ReservationDecider`, `IngestValidator`, `SupplierKey`) and are independent of any engine.

- **Identity is `(supplierId, reservationId)`.** Suppliers generate their own IDs, so a global key could let one supplier
  overwrite another's data.
- **"Same details" = room, check-in, check-out, price.** `updatedAtUtc` is deliberately excluded, so a resend that only
  bumps the timestamp is a duplicate and the details are never rewritten.
- **Decision order:**
  1. identical details -> `Unchanged`. If the incoming `updatedAtUtc` is strictly newer, only that one column is
     advanced (the freshness marker); an equal or older timestamp writes nothing and never moves the marker backwards;
  2. otherwise, older `updatedAtUtc` than stored -> `Stale` (ignored);
  3. otherwise -> `Updated` (an equal timestamp with different details is applied).
- **Why the freshness marker advances:** it stops a delayed older update from overwriting state that a newer duplicate
  re-confirmed (T1: D stored, T5: duplicate of D, T3: D' arrives late -> D' is `Stale`, final state stays D).
- **Stay dates:** `checkOut` must be strictly after `checkIn`, as instants (an equal time is rejected, the offset a timestamp is written in never matters), and the error names both values in UTC. An update with impossible dates is rejected and leaves the stored reservation untouched. A maximum stay length is not enforced (a business decision).
- **Validation:** required fields; strict JSON types and ISO 8601 timestamps with an explicit offset (see [Input format](#input-format-what-is-accepted-and-what-is-rejected)); length limits and no control characters in identifiers; years 1900 to 2200; `checkOut` after `checkIn`; price not negative, at most 2 decimals (stored as
  integer cents so equality is exact) and within range; timestamps normalised to UTC.
- **Supplier key is trimmed and case-insensitive** (`Acme` == ` acme `) for the limiter, storage and stats, so re-casing
  can't be used to multiply quota. `acme` and `acme2` stay distinct.
- **Stats:** `ingested` counts created + updated; `unchanged`, `stale` and `throttled` are reported separately. They are
  lifetime totals: purging old reservations never changes them, and `Expired` requests are not counted.
- **Retention rule (opt-in):** with `Retention:Enabled=true`, a stay whose `checkOut` is strictly before
  `now - RetentionDays` is expired: it is eligible for archive and purge, and ingest answers `Expired` for it. The two use the
  same cutoff on purpose; see [Data retention](#data-retention).

## Rate limiting

Exact rolling window, per supplier, shared across instances. The default provider is Redis.

- **Algorithm:** a sliding-window log. One Redis sorted set per supplier (score = request time in ms) and one atomic Lua
  script that prunes expired entries, denies if the count has reached the limit (returning exactly when the oldest entry
  leaves the window), or records the request. Redis `TIME` is the single clock, so instance clock skew is irrelevant.
- **Denied requests are not recorded**, so a supplier that keeps retrying can't keep itself locked out: the window
  clears 60 seconds after its last accepted request.
- **Strict isolation:** the key is per supplier with exact equality; throttling one supplier never touches another.
  Memory is bounded at about `Limit` entries per supplier, and idle keys expire on their own.
- **Reject (429), not queue.** An in-process channel isn't durable or shared across instances, a truly lossless queue
  needs a durable broker, and the spec says reject. Suppliers already retry on 429.
- **Throttle floods are cheap:**
  1. `BlockCachingRateLimiter` wraps the Redis limiter. A denial also says when the supplier's oldest entry leaves the
     window; entries only expire (never removed early), so no slot can free sooner. The instance remembers that and
     rejects the supplier locally, with no Redis call, until then. The block is measured from the time before the Redis
     call was sent and truncated, so it never outlasts the real window: it can only skip calls that would be denied.
  2. The `throttled` counter is **coalesced and awaited**: concurrent rejections for a supplier join one pending queue
     item (at most one queue slot per supplier) and each still waits for that item's commit before returning 429. A flood
     can't fill the bounded write queue and cause 503s for other suppliers, and no count is lost or acknowledged early.
- **Redis is the only rate-limiting engine in the product**, because the limit must hold across instances. An in-process
  limiter with the same exact semantics exists only under `tests/` as a verification adapter (see
  [Architecture](#architecture-and-extension-points)); `RateLimit:Provider=InMemory` is rejected at startup.

## Storage and group commit

SQLite via Dapper (WAL mode), shared by both containers through one named volume.

- **Group commit (`WriteBatcher`).** Every write goes through one writer loop per instance that drains what is queued and
  commits it as **one** `BEGIN IMMEDIATE` transaction, so a single fsync covers many requests. This removes the
  one-fsync-per-request ceiling.
- **Durability is not weakened.** A caller is acknowledged only **after** its batch commits (default `synchronous`).
  Requests still queued at a crash were never acknowledged; the supplier's retry is safe because of dedup.
- **No artificial delay:** at low load a batch holds one item (same latency as before); batches grow by themselves under
  load. Each item runs in a `SAVEPOINT`, so one failing item rolls back alone. Counter increments are summed per
  supplier per batch (N increments become one write).
- **Bounded queue** (`Sqlite:QueueCapacity`): when full the API returns 503 + `Retry-After` instead of growing without
  limit. Shutdown drains the queue before exit.
- **Concurrency safety:** the batch holds the write lock, so read-then-write cannot interleave across instances. 50
  parallel identical requests give exactly one create.

## Observability

Metrics and structured logs, both vendor-neutral. Core records standard .NET `System.Diagnostics.Metrics` instruments (part of the
framework, so Core needs no package and no exporter); only the API host decides where they go.

### Metrics

Each API instance serves `GET /metrics` in the Prometheus text format (OpenTelemetry's Prometheus exporter; note that package is
still prerelease, `1.20.1-beta.1`, while the OpenTelemetry hosting and runtime packages are stable). **The nginx proxy answers
404 for `/metrics`**: scrape the instances directly on the internal network (`api-1:8080/metrics`, `api-2:8080/metrics`); the
endpoint itself is unauthenticated, so keep it off the public internet. Counters are per instance: sum them across instances.

| Metric (Prometheus name) | Type | What it tells you |
|---|---|---|
| `reservations_ingest_requests_total{outcome}` | counter | Requests by outcome: `created`, `updated`, `unchanged`, `stale`, `expired`, `throttled`, `invalid` (readable but wrong), `malformed` (could not be read: not JSON, wrong content type, too large), `error` (limiter or storage unavailable, or unexpected) |
| `reservations_ingest_duration_seconds{outcome}` | histogram | Time to process a request, including waiting for the group commit (seconds-scale buckets, 1 ms to 10 s) |
| `reservations_ratelimit_decisions_total{result}` | counter | `allowed`, `denied`, `blocked_locally` (rejected by the local block cache, no Redis call), `unavailable` (Redis down) |
| `reservations_storage_rejections_total{reason}` | counter | Writes refused: `queue_full` (bounded queue) or `busy` (database lock timeout). Appears after the first one |
| `reservations_storage_batch_size` | histogram | Writes per group-commit transaction |
| `reservations_storage_batch_commit_duration_seconds` | histogram | Time to apply and commit one batch |
| `reservations_storage_queue_depth` | gauge | Writes waiting in the queue (the bound is `Sqlite:QueueCapacity`) |
| `http_server_request_duration_seconds`, `http_server_active_requests` | histogram, gauge | ASP.NET Core's built-in HTTP server metrics |
| `process_runtime_dotnet_*` | various | Runtime: GC, thread pool, JIT, lock contention |

- **No metric is labelled with a supplier id.** Supplier ids are unbounded, so a series per supplier would grow without limit.
  Per-supplier numbers are in the durable `GET /stats/{supplierId}`, and a throttled supplier is named in a (sampled) log line.
- Verified against real traffic through Docker: 101 created, 1 unchanged and 40 throttled requests showed up as exactly those counts
  when summed over both instances, the 40 splitting into 2 `denied` and 38 `blocked_locally`, and the batch-size histogram summed
  to the number of writes.
- The retention worker exposes no metrics (it is a run-and-exit process); it logs a summary of every run.
- **Suggested alerts (ideas; none are shipped):** any `ratelimit_decisions{result="unavailable"}` (Redis down, ingestion is
  failing closed); any `storage_rejections` (overload); `storage_queue_depth` approaching the queue capacity; ingest p99 latency;
  the share of `error` outcomes.
- Other exporters (for example OTLP to a collector) are a change in the API host only; Core and the adapters do not change. An
  architecture test fails if a telemetry package is referenced outside the API.

### Logs

The framework's structured `ILogger`. In compose the services write **one JSON object per line** (`LOG_FORMAT=simple` for plain
text), with the message template's fields as properties (for example `SupplierId`, `RetryAfterSeconds`) and logging scopes.

| Level | What | Notes |
|---|---|---|
| Information | One startup line: storage, rate limiter and limits, write batch and queue, retention policy | First thing to read when behaviour is surprising |
| Warning | `Supplier X is being throttled` | **At most once per supplier per 30 s per instance**, with a count of the lines skipped. Metrics count every throttle but cannot name the supplier, so this is where you see who |
| Error / Warning | `Rate limiter unavailable` / `Storage busy or write queue full` | **At most once per 10 s per instance**, with the exception and a count of what was skipped, so an outage does not produce one stack trace per request |
| Error | `Unhandled exception` | Never sampled |
| Debug | One line per ingest: supplier, reservation, outcome, milliseconds | **Off by default** (it is one line per request). Enable with `LOG_LEVEL_INGEST=Debug` (compose) or `Logging__LogLevel__Mize.Reservations.Api.Observability.IngestLogger=Debug` |

- Sampling is per instance and in memory. Nothing is lost for counting purposes (metrics record every event); only repeated log
  lines are skipped.
- Request and response bodies are never logged. Supplier and reservation ids appear in log lines (the per-request line, the
  throttle warning); treat them as business data.
- The retention worker logs its configuration at start and a summary of each run (pages, rows archived, objects written, rows
  deleted, rows skipped because they changed).
## Failure behaviour

| Situation | Behaviour |
|---|---|
| Redis unreachable or slow | **Fails closed:** 503 + `Retry-After: 5`, nothing written. The limiter protects the table and suppliers retry on 5xx. |
| SQLite busy or write queue full | 503 + `Retry-After: 2`. |
| Process crash | Anything acknowledged is durable. Queued, unacknowledged requests are lost; the supplier retries and dedup makes it safe. |
| Graceful shutdown | The write queue is drained and committed before exit. |
| Redis loses its data while a supplier is locally blocked | That instance may keep rejecting it for the rest of the old window (at most 60 s). |
| Malformed JSON or invalid fields | 400 with details; no stack traces or internals are returned. |
| A flood from one supplier, or an outage that fails every request | Every request is counted in the metrics, but the log gets one line per interval with a skipped count (see Observability). |
| Retention: the archive is unavailable or a write fails | **Nothing is deleted.** The run fails (a one-shot worker exits non-zero) and the next run retries from scratch. |
| Retention: crash after archiving, before deleting | Nothing lost. The rows are still in the database; the rerun writes the same archive keys again (identical content) and then deletes them. |
| Retention: a supplier updates a reservation during a pass | That row is not byte-identical to the archived copy, so it is **kept** and re-examined next run. |

## Configuration

Environment variables use `__` for `:` (for example `Redis__ConnectionString`). Values are read lazily, so tests and
hosts can override them.

| Key | Default | Meaning |
|---|---|---|
| `Storage:Provider` | `Sqlite` | Storage adapter (only `Sqlite`; the retention worker uses the same one) |
| `Sqlite:Path` | `data/reservations.db` (`/data/reservations.db` in the container) | Database file |
| `Sqlite:MaxBatchSize` | `256` | Requests per commit; `1` = no batching (compose: `MAX_BATCH=1`) |
| `Sqlite:QueueCapacity` | `2000` | Write queue bound before 503 |
| `RateLimit:Provider` | `Redis` | Rate-limiter adapter (only `Redis`; anything else fails at startup) |
| `RateLimit:Limit` / `RateLimit:WindowSeconds` | `100` / `60` | Requests per supplier per rolling window |
| `RateLimit:KeyPrefix` | `ratelimit:` | Key namespace in a shared store |
| `Redis:ConnectionString` | `localhost:6379` (`redis:6379` in compose) | Any Redis-protocol server |
| `Logging:Console:FormatterName` | `simple` (`json` in compose) | Console log format: `simple`, `json` or `systemd` (compose: `LOG_FORMAT`) |
| `Logging:LogLevel:Mize.Reservations.Api.Observability.IngestLogger` | `Information` | Set to `Debug` for one log line per ingest request (compose: `LOG_LEVEL_INGEST`) |
| `Retention:Enabled` | `false` | Master switch. Read by the API (the `Expired` guard) **and** by the retention worker; keep them identical |
| `Retention:RetentionDays` | `365` | Stays that ended more than this many days ago are expired. Use far more than suppliers can resend or reorder |
| `Retention:BatchSize` / `Retention:MaxBatchesPerRun` | `1000` / `100` | Rows per page and pages per run (a run moves at most their product) |
| `Retention:DryRun` | `false` | Worker only: report what would be archived and deleted, change nothing |
| `Retention:RunIntervalMinutes` | `0` | Worker only: `0` = run once and exit (schedule it), `n` = repeat every n minutes |
| `Retention:AllowDeleteWithoutArchive` | `false` | Worker only: required to purge with `Archive:Provider=None` (deleting with no copy) |
| `Archive:Provider` | `None` | Worker only: `None`, `FileSystem` or `S3`. With retention enabled, `None` is refused unless explicitly allowed |
| `Archive:FileSystem:Path` | `archive` (`/archive` in the container) | Archive directory |
| `Archive:S3:Bucket` / `Prefix` / `Region` | `reservation-archive` / empty / `us-east-1` | Bucket, optional key prefix inside it, region |
| `Archive:S3:ServiceUrl` | empty (= AWS S3) | Set for an S3-compatible server such as MinIO (for example `http://minio:9000`) |
| `Archive:S3:AccessKey` / `SecretKey` | empty | Else the AWS default credential chain (environment, profile, instance or task role) |
| `Archive:S3:ForcePathStyle` / `CreateBucketIfMissing` | `true` with a ServiceUrl, else `false` / `false` | Path-style addressing; create the bucket on first write (convenient locally, leave off on AWS) |

## Architecture and extension points

`Core` holds the business rules and the ports; adapters implement the ports; the `Api` host wires them from
configuration. Dependencies point inward: adapters depend on Core, never the reverse, and Core has no infrastructure
packages. The Api project is the composition root, so it alone references the adapters.

```
   Api (endpoints, composition root)                       Retention.Worker (archives and purges; separate process)
     |  chooses adapters by config                           |  chooses adapters by config
     +------------------+---------------+                    +----------------+---------------------+
     v                  v               |                    v                v                     v
 Storage.Sqlite   RateLimiting.Redis /   |             Storage.Sqlite   Archive.FileSystem     Archive.S3
                  RateLimiting          |                    \                |                     /
        \               |               |                     +---------->  Core  <---------------+
         +----------->  Core  <---------+
  ports: IReservationRepository, IStatsRepository, IStorageInitializer, IReservationRetentionStore,
         IReservationArchive, IRateLimiter
  rules: ReservationDecider, IngestValidator, IngestService, RetentionPolicy, RetentionService, ArchiveLayout/Format
```

| Port (in Core) | Default adapter | Other adapter shipped | Config switch |
|---|---|---|---|
| `IReservationRepository`, `IStatsRepository`, `IStorageInitializer` | `Storage.Sqlite` (Dapper, group commit) | none selectable (see the verification adapter below) | `Storage:Provider` = `Sqlite` |
| `IRateLimiter` | `RateLimiting.Redis` (behind a local block cache) | none selectable (see the verification adapters below) | `RateLimit:Provider` = `Redis` |
| `IReservationRetentionStore` | `Storage.Sqlite` (same project and database as the storage port) | none selectable | `Storage:Provider` = `Sqlite` |
| `IReservationArchive` (worker only) | none: `Archive:Provider` defaults to `None` | `Archive.FileSystem`, `Archive.S3` (AWS S3 or an S3-compatible server) | `Archive:Provider` = `None` \| `FileSystem` \| `S3` |

**Supported engines in the product: SQLite (storage), Redis (rate limiting) and, for the optional archive, a directory or S3
(any S3-compatible server), and nothing else.** `Api/Composition/InfrastructureRegistration.cs` is the only place in the API
that names concrete adapters, and the worker has its own small switch for the archive. An unknown provider fails
at startup with a clear message, for example `Storage:Provider=InMemory` is rejected with
`Unknown Storage:Provider 'InMemory'. Available: Sqlite.` and `RateLimit:Provider=InMemory` with
`Unknown RateLimit:Provider 'InMemory'. Available: Redis.`

**Verification adapters (not part of the product).** Two in-process adapters live under `tests/` purely to prove the ports
are engine-neutral and swappable:

- `tests/Mize.Reservations.Storage.InMemory`: a second, completely different storage engine (no SQL, no files, not
  durable, not shared across instances), written against `Core` only. It implements the retention port too.
- `tests/Mize.Reservations.RateLimiting.InMemory`: an in-process sliding-window limiter (single instance only), written
  against `Core` and the shared options type.

Neither is selectable by configuration, referenced by the Api, or in the Docker image. They are verified three ways:

1. **Same contract, different engine:** they pass the same `StorageContract` / `RetentionStoreContract` /
   `RateLimiterContract` suites as SQLite and Redis, with no change to any port.
2. **Whole API on swapped adapters:** the API runs end to end with both swapped in through the ports, with no SQLite and
   no Redis, and a test asserts that no SQLite file is ever created.
3. **Enforced structure:** architecture tests read the compiled assemblies and fail if Core references any other project
   or an engine package, if an adapter references another adapter or the host, if an engine package leaks out of its own
   adapter, or if the product references either verification adapter (with a positive control so the scan can't pass
   vacuously).

What this does and does not prove: it shows the ports and the contract carry no SQLite-specific assumptions that an
engine with different mechanics (a lock instead of transactions, no files) cannot meet, and that the host depends only on
the ports. An in-memory engine is the easy case, though. It does not exercise network failures, crash durability or a real
transactional database, so a production second engine (for example Postgres) would still be the stronger proof.

**The business rules live once, in Core.** `ReservationDecider.Decide(existing, incoming)` returns what to do (insert,
update, touch the timestamp, nothing) and the outcome; `IngestOutcome.ToCounter()` maps outcomes to counters. A storage
adapter only loads the current state, calls the decider inside its own atomic mechanism, and applies the action. It never
re-implements the rules.

**How to add an engine** (Postgres, SQL Server, DynamoDB, Memcached, a cloud cache, ...):

1. Create a project for it (`Storage.Postgres`, `RateLimiting.Memcached`, ...) referencing only `Core`.
2. Implement the port. The comments on the interfaces in `Core/Abstractions` state the contract (atomicity, durability
   before acknowledging, exact counters, no recording of denied requests, supplier isolation, ...).
3. Add an `AddXxx(IServiceCollection)` extension that registers it (read configuration lazily, as the existing ones do).
4. Add one `case` to `InfrastructureRegistration`.
5. Prove it with the contract suite: in a test project derive from `StorageContract` and `RetentionStoreContract` (storage),
   `ArchiveContract` (archive) or `RateLimiterContract` / `DistributedRateLimiterContract` (limiter; the latter if state is
   shared across instances) and implement the one factory method. xUnit then runs every behavioural test against your adapter. See `SqliteStorageContractTests` and
   `RateLimiterContractTests` for the pattern, and `tests/Mize.Reservations.Storage.InMemory` as the smallest complete
   example of a storage adapter (about 100 lines). If the contract passes, it is a drop-in.

**Swapping Redis** for another Redis-protocol service (ElastiCache, Azure Cache for Redis, Memorystore) changes only the
connection string. A different shared store is a new `IRateLimiter` (steps above).

**Moving to the cloud.** Nothing in the host is machine-specific: configuration comes from environment variables, the
container runs as a non-root user and is stateless apart from the storage adapter, and `/health` serves probes.

| Today | Cloud equivalent | What changes |
|---|---|---|
| nginx container | Application load balancer / ingress | nothing in the app |
| Redis container | Managed Redis-protocol cache | the connection string |
| SQLite file on a shared volume (one host) | Postgres / SQL Server / managed SQL | new `Storage.*` adapter + `Storage:Provider`; Core untouched |
| `docker compose` | Container service / Kubernetes | deployment files only |
| Directory archive on a volume | S3 (or any object store with an S3 API) | `Archive:Provider=S3` and its settings; no code change |
| Retention worker run by hand or on an interval | Scheduled job (cron, Kubernetes CronJob), concurrency forbidden | deployment files only; the worker is already a run-and-exit process |

**Load balancing and failover.** nginx round-robins across `api-1` and `api-2` (measured: an even split). The API instances are
stateless apart from the shared database, so no session stickiness is needed (each instance's local block cache only affects
efficiency, never correctness).

- **Failover, measured.** With one instance stopped, every request still succeeded through the other. A request is retried on the
  other instance only when the connection failed (nothing was sent yet); a POST that was already sent is never replayed, and an
  in-flight request on an instance that dies fails, so the supplier retries (safe: ingest is idempotent).
- **A defect found and fixed here:** with no connect timeout set, the first request after an instance stopped hung for **38.6 s**
  (nginx's 60 s default) before failing over. `proxy_connect_timeout 2s` brings it to **2.0 s**.
- **What remains:** nginx's health checking is passive. While an instance is down, about one request per 10 s (`fail_timeout`)
  pays that 2 s probe. Active health checks (nginx Plus, or a managed load balancer probing `/health`) remove it.
- **nginx is a single point of failure** in this setup, and it resolves container names once at startup (a standard nginx
  behaviour I did not trigger here): if an instance is recreated with a different IP, restart nginx. In the cloud a managed load
  balancer replaces it (TLS termination, active health checks, several zones).
- **A load balancer adds redundancy, not write capacity.** Every instance still writes to the one SQLite file, which is the
  throughput limit (see Storage and group commit). More instances help availability and rolling deploys, not the ceiling.
**Deliberately not done** (no second production implementation exists to justify them): a plugin loader, a generic
message-bus abstraction, and abstracting the group-commit writer (it is an internal detail of the SQLite adapter; another
engine brings its own atomicity and batching strategy behind the same port). There is also no second production storage
engine: SQLite is the only supported one, and the in-memory adapter is verification-only. The archive adapters (directory, S3)
are real, supported adapters, but only used by the optional retention worker.

## Testing

```
dotnet test tests/Mize.Reservations.UnitTests          # no dependencies, about 10 s (281 tests)
dotnet test tests/Mize.Reservations.IntegrationTests   # needs Docker (Testcontainers starts a real Redis), about 1 min (161 tests)
dotnet test tests/Mize.Reservations.Archive.S3.Tests    # 13 tests, skipped unless you point it at an S3 server (see Data retention)
```

**The rule that decides where a test lives.** An integration test must run the *full real path*: a real HTTP host, the real Redis
limiter and real SQLite (the retention tests: real SQLite plus the real archive). If a test swaps an adapter for an in-memory one, or
injects a fake that throws, it is not proving the real path, so it is a unit test, even when it starts the host. Likewise a test
that only checks how one input is parsed belongs next to the parser, not behind HTTP.

### Unit tests (`tests/Mize.Reservations.UnitTests`, no Docker)
- **Rules:** `ReservationDecider`, `IngestValidator` and `IngestService` (with fakes), the strict payload parser
  (`IngestRequestParserTests`, the full matrix of wrong types, timestamps, prices, identifier limits and years), checkOut-after-checkIn
  by instant, the bounded body reader (`RequestBodyReader`: exact limit, no declared length, endless stream, cancellation).
- **Error handling:** `ExceptionHandlingMiddleware` called directly (framework 4xx keeps its status with a generic message, a client
  disconnect is swallowed, any other cancellation is a 500) and `UnexpectedErrorApiTests` (a repository that throws, injected into the
  host: generic 500, no leak, logged with its exception every time, counted as `error`).
- **Bounded memory:** `LogSampler` and `BlockCachingRateLimiter` forget quiet keys and expired blocks once past 10,000 entries, and
  never forget a key with a suppressed count or a block still in force (manual clock).
- **Limiter decorator and the verification limiter** against a manual clock; **architecture rules** (Core depends on nothing,
  adapters never on each other, engine packages confined, the product never references the verification adapters).
- **Swappability:** `FullySwappedApiTests` (the whole API on in-memory storage and limiter: no SQLite, no Redis, SQLite never
  touched) and `RateLimiterSwapApiTests` (limiter swapped through the port; `RateLimit:Provider=InMemory`, `Storage:Provider=InMemory`
  and any unknown provider fail fast at startup). The in-memory adapters also pass the same contract suites
  (`InMemoryAdapterContractTests`, `InMemoryStorageContractTests`).
- **Group commit** (`WriteBatcherTests`, real SQLite file, no HTTP): a failing item doesn't affect its batch, bounded queue, shutdown
  drain, durable before acknowledged, and the coalesced throttle counter (a 5,000-request flood fits in one queue slot, counts exact,
  doesn't starve other suppliers); `WriteBatcherMetricsTests` checks the batch-size histogram, timing and queue-depth gauge.
- **Retention logic:** `RetentionService` with fakes (archive-before-delete order, archive failure, crash and rerun, changed-row
  skip, paging, dry run, disabled) and the `Expired` guard (strict boundary, still costs quota, `Invalid` stays `Invalid`).

### Contract suites (`tests/Mize.Reservations.Contracts`)
Engine-agnostic, observing state only through the ports, run against every adapter. The real adapters run in the integration project,
the in-memory ones in the unit project.
- `StorageContract`: SQLite with batching on and off, and in-memory. Every dedup outcome, the freshness-marker rule, stale handling,
  supplier independence, 50 concurrent identical requests, exact counters under heavy concurrency.
- `RateLimiterContract` (in-memory) and `DistributedRateLimiterContract` (Redis alone, and Redis behind the block cache, the composition
  the app uses): exactly the limit allowed, rolling (not fixed-bucket) window, denied requests not consuming quota, supplier isolation,
  300 concurrent requests across two instances allowing exactly 100.
- `RetentionStoreContract` (SQLite and in-memory): cutoff boundary, ordering, keyset paging, delete-only-if-unchanged.
  `ArchiveContract` (directory archive and, when you provide a server, S3: 13 of 13 pass against `pgsty/minio`): round trip,
  idempotent rewrite, hostile supplier ids, no escape from the root, 5,000-row batch.

### Integration tests (`tests/Mize.Reservations.IntegrationTests`, full real path)
All use `ApiInstance` (an in-process host with the real composition, pointed at the Redis container and a temp SQLite file). Several
read the SQLite file directly, so they check what was *stored*, not only the status code.
- `IngestApiTests`: create / duplicate / concurrent identical payloads across two instances create once; the 101st request is 429 with
  `Retry-After`; a 300-request throttle flood is counted exactly and doesn't disturb another supplier; 150 parallel requests across two
  instances give exactly 100 accepted; supplier id case and whitespace share one quota; a Redis outage returns 503 and writes nothing.
  If the shared-limit test ever fails, its message lists the status counts and any unexpected response bodies.
- `MainFlowDatabaseTests`: the spec scenario step by step with the stored row asserted after each step (normalised supplier, UTC times,
  price in cents, duplicate leaves the row identical, timestamp-only update touches only `UpdatedAtUtc`, update in place, stale ignored,
  composite key independence, invalid stores nothing, DB stats equal API stats); the 101st request leaves exactly 100 rows; two instances
  under 150 parallel requests store exactly the accepted ids; 40 identical parallel requests make 1 row; Redis down writes nothing.
- `FailureModesApiTests`: a full write queue answers only 201 or 503 with `Retry-After`, stores exactly what was accepted and recovers;
  a database locked by another connection answers 503 after the busy timeout, reads keep working, and it recovers when released.
- `MalformedInputApiTests` and `StayDatesApiTests`: one case per *kind* of answer (empty or non-JSON body, non-object, wrong content type
  415, oversize 413, missing supplier, bad timestamp, bad price, checkOut not after checkIn), each a problem+json that names the field,
  stores nothing and costs quota where it should. The full matrix is in the parser and validator unit tests.
- `RetentionApiTests`: with retention on, an update for an expired stay is `200 Expired` and never stored; off by default.
- **Observability** (`MetricsEndpointTests`, `LoggingApiTests`): `/metrics` returns Prometheus text with the expected series, seconds-scale
  buckets and no supplier label; a throttle flood yields one warning naming the supplier; a limiter outage yields one error line with its
  exception while every request is still a 503 and is counted; per-request lines are off by default and appear with Debug; startup logs one
  summary. Meter assertions are lower bounds because parallel hosts share meters by name.
- **Retention end to end** (`RetentionEndToEndTests`, real SQLite and real directory archive): old rows move and recent ones stay; a crash
  after archiving loses nothing and creates no duplicate objects; a supplier update mid-pass keeps the live row; an unavailable archive
  deletes nothing; a late update after a purge is `Expired` and not resurrected; 5,000 rows move in batches; dry run changes nothing;
  incremental auto-vacuum, the covering index and space reclaim. `S3RetentionEndToEndTests` runs the same scenarios against S3: four are
  skipped with instructions unless `MIZE_S3_ENDPOINT` (or `MIZE_S3_BUCKET`) is set, a fifth needs no server (an unreachable S3 endpoint must
  delete nothing). All pass against `pgsty/minio` and leave the bucket empty.

### Timing-sensitive tests
The rate-limiter contract tests use the real clock. Only the rolling-window test has two-sided timing (one batch must have expired while the
next must not have), and a stalled test host once stretched its sleeps from 3.3 s to 5 s and made a correct limiter look wrong. It now
measures its own timings and repeats the attempt when the host was too slow for the scenario to be valid; a real bug still fails every valid
attempt (checked by turning the limiter into a fixed bucket, which the test then caught). The other real-clock tests only wait for "at least
this long", so a stall cannot break them.

### Coverage and manual checks
- `tools/coverage_report.py` summarises a coverlet result for the service's own code: run
  `dotnet test tests/Mize.Reservations.IntegrationTests --collect:"XPlat Code Coverage" --results-directory <dir>`, then
  `python tools/coverage_report.py <dir> --main-flow --lines` (classes on the ingest/stats path and their uncovered lines; `--min 99.9`
  shows only classes below 99.9%). Ignore `LoggerMessage.g.cs`: it is source-generated.
- `tools/throttle-test.ps1 [-Supplier X]` checks the rate limit against a running stack (105 requests: 100 accepted, 5 throttled, an
  immediate follow-up still 429 with `Retry-After`, another supplier unaffected, stats match). `tools/try-flow.sh` walks every behaviour with curl.

## Alternatives considered

| Alternative | Decision | Why |
|---|---|---|
| In-process rate limiter in the product | Rejected; kept as a verification-only adapter under `tests/` and blocked at startup | State is per instance, so N instances would allow N x the limit. The limit must be shared (Redis). It exists only to prove the `IRateLimiter` port is swappable. |
| Token bucket / sliding-window counter | Rejected | They don't enforce "100 in any rolling 60 s" exactly (a bucket can admit about 2x the limit across a window). The sliding-window log is exact. |
| Queue throttled requests instead of 429 | Rejected | An in-process channel isn't durable or shared; the spec says reject; suppliers retry on 429. |
| `PRAGMA synchronous=NORMAL` | Rejected | Faster, but a power loss can drop recently committed transactions. We don't trade durability for speed. |
| Kafka (partition key `supplier\|reservationId`) | Rejected for this scope | Makes ingest asynchronous (202), so the API could no longer return Created/Updated/Unchanged as the spec asks. It doesn't remove the SQLite single-writer bottleneck, the rate limit still needs Redis, and per-key ordering is by arrival order, so the `updatedAtUtc` stale check is still needed. Heavy infrastructure for a scoped exercise. Reasonable later with a multi-writer database and high volume. |
| Redis Streams / Redis as the queue or source of truth | Rejected | Redis persistence (`appendfsync everysec`) can lose about a second of acknowledged data on a crash; `always` is slow. A TTL does nothing for durability. |
| Redis fingerprint cache in front of SQLite | Deferred | See below. Group commit already removed most of the contention it would address. |
| Read-only duplicate check outside the write lock | Superseded | Group commit covers duplicates, creates, updates and counter writes without extra read-path races. |
| Automated purge and archive of old records | Implemented, opt-in and off by default | Archive first, delete only identical rows, same-keys-on-retry, a resurrection guard, a separate worker. Directory and S3 archives. See [Data retention](#data-retention). |
| MinIO as the S3 server | Used for development and CI: the `pgsty/minio` community fork, pinned by digest, in an opt-in compose profile | The official images are no longer pullable. The adapter is server-agnostic; its contract suite passes 13 of 13 against this server. Real S3 remains the archive of record and has not been tested here. |
| Retention inside the API process | Rejected | Several API instances would race on it; a separate run-and-exit worker is schedulable and has one writer. |
| S3 (object storage) as the primary store | Rejected | A GET plus a conditional PUT per upsert, no cross-object transactions, no atomic counters, no querying; would give up group commit and exact stats. It fits as an archive, not as the dedup store. |
| Per-instance fallback limiter when Redis is down | Not done | Would keep ingestion running with approximate protection instead of failing closed. A deliberate availability-versus-exactness choice; failing closed is defensible. |

### Redis fingerprint cache (deferred)

Idea: hash the full normalised payload (including `updatedAtUtc`) per `(supplier, reservationId)`; a hit returns
`Unchanged` without touching SQLite; the entry is set only **after** the SQLite commit, with a TTL of roughly 10-60
minutes (the supplier retry horizon), and written with a Lua compare-and-set on `updatedAtUtc` so an older version never
replaces a newer one. A miss, eviction or Redis restart simply falls back to SQLite, which stays the only source of truth.

- **Pros:** exact retries skip the database entirely; less contention on a duplicate-heavy workload; a miss is always safe.
- **Cons:** about 0.3 ms extra for every unique request (a Redis round trip, unless folded into the limiter call); a second
  store that can disagree with SQLite (cache writes can land in a different order than commits); it only catches exact
  repeats (a duplicate with a newer timestamp still goes to SQLite); it does not help unique inserts or updates; more code
  and more tests (hit, miss, eviction, ordering race).
- **Why deferred:** with group commit the data layer already handles duplicates at roughly 6,500-19,700 req/s, so the
  remaining benefit is not measurable in our setup, while the consistency risk and the extra hop are real. Revisit only if
  a production-like measurement shows the duplicate path is still the bottleneck.

## Data retention

**Opt-in, and off by default.** Out of the box nothing is archived or deleted. The `Reservations` table holds one row per
distinct `(supplier, reservationId)`, updated in place, so it grows with the number of distinct reservations, not with
request volume (duplicates, updates and stale resends add no rows). `SupplierStats` is one row per supplier, the Redis
limiter keys expire on their own, and the SQLite write-ahead log is checkpointed automatically. A rough sizing estimate (not a
measurement): about 200-300 bytes per reservation including its index, so roughly 250 MB per million and 25 GB per 100 million.

Turning retention on (`Retention:Enabled=true`) does two things: a separate **retention worker** moves old reservations to an
archive, and the API starts answering `Expired` for updates to stays that old (the resurrection guard below).

### How a retention run works

The worker (`Mize.Reservations.Retention.Worker`) reads **pages** of expired rows (stays whose `checkOut` is before
`now - RetentionDays`), ordered by `(checkOut, supplier, reservationId)` with keyset paging over a covering index. For each page:

1. **Archive.** Split the page into one object per (check-out month, supplier) and write each one. The archive call returns only
   when the object is durable.
2. **Delete.** Remove each row **only if it is still byte-identical to the archived copy** (every detail and the timestamp),
   through the group-commit writer in one small transaction.
3. **Reclaim** freed space in small steps, once per run.

What that guarantees:

- **A row is never deleted before its archive copy is durable.** If the archive write fails, nothing is deleted and the run fails
  (a one-shot worker exits non-zero).
- **Every step is safe to repeat.** After a crash between archiving and deleting, the rows are still there; the rerun archives them
  again to the **same keys** (the key is a hash of the content, so identical content gives an identical key) and then deletes.
- **A live reservation is never deleted by a stale pass.** If a supplier updates a row after it was archived (even moving the stay
  to a future date), the row no longer matches the archived copy, so it is kept and re-examined next run.
- **The archive is at-least-once.** A record can appear in more than one object (for example a row that changed mid-run leaves its
  older version in the archive), so readers dedupe by `(supplierId, reservationId, updatedAtUtc)`.
- **One runner.** It is a separate process, not part of the API, so several API instances can't race on it. Run it once and exit
  (schedule it with cron or a Kubernetes CronJob with concurrency forbidden), or set `Retention:RunIntervalMinutes`.
- **Safe defaults.** `Retention:DryRun=true` reports what would happen and changes nothing. With retention enabled and
  `Archive:Provider=None` the worker refuses to start unless `Retention:AllowDeleteWithoutArchive=true`, so deleting with no copy
  is always a deliberate decision.

### The `Expired` guard (resurrection protection)

Once a row is purged, a late retry or reordered update for it would be stored as a brand-new reservation. So with retention on,
ingest answers **200 `Expired`, `changed:false`** for any reservation whose `checkOut` is before the same cutoff, and never
stores it. It is a 200, not an error, because suppliers retry on non-2xx and would loop. It still costs rate-limit quota, and
`Expired` requests are not counted in the stats. Consequences to know about:

- **The API and the worker must use the same `Retention:Enabled` / `Retention:RetentionDays`** (compose passes the same variables
  to both).
- **Back-dated imports older than the cutoff are rejected as `Expired` by design.**
- Choose `RetentionDays` far longer than suppliers can resend or reorder (days to weeks), so a year or more is safe.

### Archive format and layout

Each object is gzipped JSON Lines, one reservation per line, versioned and self-describing so other tools can read it without
this code:

```
reservations/v1/checkout=YYYY-MM/supplier=<encoded supplier>/<batch id>.jsonl.gz
{"v":1,"supplierId":"acme","reservationId":"r1","roomId":"room1","checkIn":"...Z","checkOut":"...Z","priceCents":45000,"updatedAtUtc":"...Z"}
```

The supplier id is encoded to a single safe path segment (only `a-z 0-9 _ -` pass through; everything else, including `.` and
`/`, becomes `%XX`), which blocks path traversal; every adapter also validates keys (under `reservations/v1/`, no `..`). The
Hive-style `checkout=` and `supplier=` partitions let query engines such as Athena or DuckDB prune by month and supplier (the
layout is built for that; querying it is not covered by tests here).

### Running it

```
# one run: archive to the `archive-data` volume, then delete  (set the SAME values on the API for the Expired guard)
RETENTION_ENABLED=true RETENTION_DAYS=365 docker compose --profile archive run --rm retention
# look first, change nothing
RETENTION_ENABLED=true RETENTION_DRY_RUN=true docker compose --profile archive run --rm retention
```

Without Docker, run `src/Mize.Reservations.Retention.Worker` with the same `Retention__*`, `Archive__*` and `Sqlite__Path` settings.
It uses the same SQLite database as the API (its own writer, small batches).

### Archive backends

- **`FileSystem`** (default for compose): each object is written to a temp file in the same directory, flushed to disk, then
  atomically renamed into place, so a reader never sees a partial object.
- **`S3`** (AWS S3 or any S3-compatible server): the AWS SDK with a configurable endpoint (`Archive:S3:ServiceUrl`) and
  path-style addressing for non-AWS servers. A successful PUT is durable; integrity is checked twice (the request carries a
  Content-MD5 so the server rejects a corrupted body, and the returned ETag is compared with our own MD5 of the data).

**Verification status of S3 and MinIO.**

- **Verified against a live MinIO.** The 13-test archive contract suite passes (13 of 13) against the `pgsty/minio` image, run
  both as a plain container and as the compose service defined in this repository. The real worker was also run through Docker
  with `Archive:Provider=S3` against it: 4 rows were archived to 4 objects and deleted; the objects were listed with the S3 API
  and read back (valid gzip JSON Lines, `Content-Type: application/gzip`, an MD5-style ETag); and after re-creating a purged row,
  a rerun overwrote the same key (the key list before and after was identical). The retention service on real SQLite with the
  S3 archive is also covered by automated end-to-end tests (`S3RetentionEndToEndTests`, 5 of 5 pass against it). The tests
  leave nothing behind (a bucket listing afterwards shows 0 objects).
- **Not verified against AWS S3.** MinIO is S3-compatible, not identical: conditional writes, lifecycle rules, storage classes,
  IAM and KMS-encrypted ETag semantics differ. Before relying on it in production, run the same suite against a real bucket
  (`MIZE_S3_BUCKET=<bucket>` with AWS credentials) and smoke-test the worker.
- **The MinIO image is a community fork, not the official MinIO.** The official images (`minio/minio` on Docker Hub,
  `quay.io/minio/minio`) can no longer be pulled. `pgsty/minio` is built by a third party (pgsty), licensed AGPL-3.0, and is
  **pinned by digest** in `docker-compose.yml`, so what runs is exactly what was tested (version `RELEASE.2026-08-04T00-00-00Z`).
  Review its provenance and licence for your use. It is for development and CI; real S3 is the archive of record in production.
  The adapter is server-agnostic, so choosing a different S3-compatible server later is a configuration change.
- **Credentials:** the compose service has **no default password** (MinIO refuses to start without one of at least 8
  characters) and is published on localhost only.

```
# start MinIO (the compose service; password required)
MINIO_ROOT_PASSWORD=<8+ chars> docker compose --profile minio up -d minio

# archive to it with the worker (inside the compose network it is reachable as http://minio:9000)
RETENTION_ENABLED=true ARCHIVE_PROVIDER=S3 S3_ENDPOINT=http://minio:9000 S3_ACCESS_KEY=mizeminio S3_SECRET_KEY=<password> \
  S3_BUCKET=reservation-archive S3_CREATE_BUCKET=true docker compose --profile archive run --rm retention

# run the S3 contract tests against it (skipped, with instructions, when MIZE_S3_ENDPOINT / MIZE_S3_BUCKET are not set)
MIZE_S3_ENDPOINT=http://127.0.0.1:9000 MIZE_S3_ACCESS_KEY=mizeminio MIZE_S3_SECRET_KEY=<password> \
  dotnet test tests/Mize.Reservations.Archive.S3.Tests        # MIZE_S3_BUCKET=<bucket> instead of an endpoint targets AWS S3
```

  (PowerShell: set `$env:NAME='value'` for each variable first.) Each test uses its own random key prefix and cleans up after
  itself.
### Operational notes

- **The retention index is built on first use, not at startup**, so deployments that never enable retention pay nothing for it on
  inserts. Building it takes the write lock for as long as it takes to scan the table; on a very large existing table run the
  first pass during a quiet period.
- **Space is only returned to the file system for databases created with `auto_vacuum=INCREMENTAL`** (every new database is).
  For an existing database the reclaim step is a no-op, and a one-time full `VACUUM` (which blocks writers) switches it on.
- **Stats are lifetime totals**, unaffected by purges. There is no restore tool yet; the format is open, so one is easy to add.
- **Do not delete rows by hand**: it bypasses the service and has the resurrection risk above.
- **Why not S3 as the primary store:** every upsert would be a GET plus a conditional PUT per reservation, with no transactions
  across objects, no atomic counters and no querying. S3 fits as an archive, not as the dedup store.

## Limitations

- **Retention is opt-in.** Until it is enabled, nothing is ever purged and storage grows with the number of distinct
  reservations. The S3 archive adapter is verified against a MinIO fork (`pgsty/minio`) but not against AWS S3 (see [Data retention](#data-retention)).
- SQLite on a shared volume works because both containers run on **one host** (WAL + busy timeout). It will not work on a
  network filesystem; production would use SQL Server/Postgres via a new storage adapter, with Core untouched.
- The SQLite write lock is database-wide. Group commit makes each instance take it once per batch instead of once per
  request, but two instances still alternate on it.
- The local block cache is per instance and in memory (see Failure behaviour for the Redis data-loss caveat).
- The write queue is in memory: on a crash, queued-but-unacknowledged requests are lost (the supplier retries).
- Running the API outside compose still needs a Redis (there is no in-process limiter in the product).
- No lineage between a re-booked reservation's old and new IDs (out of scope).
- No auth, as agreed. Observability is metrics and structured logs only: no tracing, no dashboards or alert rules are shipped, and
  `/metrics` is unauthenticated (it relies on the proxy and network isolation). The Prometheus exporter package is prerelease.

## Performance measurements

Data layer (`dotnet run -c Release --project tools/DbBench`; real SQLite file on the host disk, no HTTP):

| Mode | Concurrency | batch=1 (before) | batch=256 (group commit) | p99 before -> after |
|---|---|---|---|---|
| duplicates | 20 | 467 req/s | 6,548 req/s | 77 -> 4.7 ms |
| duplicates | 100 | 870 req/s | 19,712 req/s | 128 -> 6.6 ms |
| unique inserts | 20 | 735 req/s | 4,321 req/s | 37 -> 9.4 ms |
| unique inserts | 100 | 760 req/s | 8,154 req/s | 153 -> 23.6 ms |
| sequential (conc 1) | 1 | ~380-680 req/s | ~720-830 req/s | no regression at low load |

End to end through nginx on Docker Desktop (`tools/loadtest.ps1`) the gain is much smaller (about 1.3-1.9x throughput,
about 3x lower p99), because that setup has a ~20 ms floor per request even for `/health` (client + port forwarding +
nginx) that dominates. Treat the data-layer table as the measure of what group commit changes. The data-layer run is a
single process with one writer on the host disk (not the Docker volume), so absolute numbers will differ elsewhere; with
two instances the combined gain is smaller because they still alternate on the SQLite lock.

`tools/floodtest.ps1` hammers one supplier far past its limit while calm suppliers send normal traffic. It checks that the
calm suppliers see no 429/503 and that the noisy supplier's `throttled` stat equals the 429s the client received. In the
last run all counts were exact (100 accepted, 2,900 rejected and counted, all 200 calm requests accepted). Its latency
numbers are dominated by the test client and are not a benchmark.

## Repository layout

```
README.md, docs/DESIGN.md                     the half-page overview; this document
src/Mize.Reservations.Core                    models, ports, business rules (decider, validator, ingest service, retention service, metrics). No infrastructure.
src/Mize.Reservations.Storage.Sqlite          SQLite + Dapper adapter: repositories, schema init, WriteBatcher (group commit), retention store, DI
src/Mize.Reservations.RateLimiting            generic limiter parts: block-cache decorator, options, DI
src/Mize.Reservations.RateLimiting.Redis      Redis sliding-window limiter (Lua) + DI; the only project that references Redis
src/Mize.Reservations.Archive.FileSystem      directory archive adapter (atomic, flushed writes); Core only
src/Mize.Reservations.Archive.S3              S3 / S3-compatible archive adapter (AWS SDK); the only project that references the AWS SDK
src/Mize.Reservations.Retention.Worker        separate run-and-exit (or interval) process: archives, then purges; chooses the archive by config
src/Mize.Reservations.Api                     endpoints, strict payload parser, bounded body reader, error middleware, logs and log sampling, composition root (provider switches)
tests/Mize.Reservations.Contracts             reusable, engine-agnostic contract suites (Storage, RetentionStore, Archive, RateLimiter) and the meter probe
tests/Mize.Reservations.Storage.InMemory      VERIFICATION ONLY: second storage engine (Core-only) proving the ports are swappable
tests/Mize.Reservations.RateLimiting.InMemory VERIFICATION ONLY: in-process limiter proving IRateLimiter is swappable (blocked in the app)
tests/Mize.Reservations.UnitTests             no Docker: rules, parser matrix, middleware, bounded memory, group commit (real SQLite file), swapped-adapter API tests, in-memory contracts, architecture rules
tests/Mize.Reservations.IntegrationTests      full real path (real host, Redis, SQLite): API flows and stored rows, failure modes, malformed input, observability, retention end to end, real-adapter contracts (Docker for Redis)
tests/Mize.Reservations.Archive.S3.Tests      ArchiveContract against YOUR S3-compatible server via env vars; skipped when none is configured
tools/try-flow.sh, tools/show_db.py           walk through every behaviour with curl; show the rows and counters in the database
tools/throttle-test.ps1                       checks the rate limit against a running stack (100 accepted, rest 429, isolation, stats)
tools/run-local.ps1                           run the API on Windows with the database on your disk (to open it in Rider)
tools/loadtest.ps1                            end-to-end concurrent load test against the compose stack
tools/floodtest.ps1                           throttle-flood test: one noisy supplier vs calm ones; checks counts are exact
tools/coverage_report.py                      summarise a coverlet result for the service's own code and list uncovered lines
tools/DbBench                                 data-layer benchmark (batching off vs on)
Dockerfile, docker-compose.yml, nginx/        packaging: two API instances behind nginx, Redis, shared SQLite volume; `--profile archive` adds the worker, `--profile minio` an S3-compatible server
```
