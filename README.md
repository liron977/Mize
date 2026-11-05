# Reservation Ingest Service

ASP.NET Core (.NET 8) API that ingests supplier reservations into SQLite, with Redis rate limiting.

**Run:** `docker compose up --build` → http://localhost:8080 (nginx → 2 API instances → Redis + shared SQLite)

```bash
curl -X POST localhost:8080/api/reservations/ingest -H "Content-Type: application/json" -d '{
  "supplierId":"acme","reservationId":"r1","roomId":"room1","checkIn":"2026-08-01T00:00:00Z",
  "checkOut":"2026-08-03T00:00:00Z","price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}'
curl localhost:8080/api/reservations/stats/acme     # ingested / throttled / unchanged / stale
```

| Request | Answer |
|---|---|
| New reservation | `201 Created` |
| Exact resend | `200 Unchanged` (nothing changed) |
| Same id, different details, newer `updatedAtUtc` | `200 Updated` (older one: `Stale`, ignored) |
| Over 100 requests / rolling 60 s per supplier | `429` + `Retry-After` |
| Bad input | `400` / `413` / `415` problem+json that says what is wrong |
| Redis or database unavailable | `503` + `Retry-After` (never silently accepts) |

**Design:** a reservation is identified by supplier + reservation id. Core rules sit behind interfaces, so storage (SQLite, group commit), rate limiter (Redis sliding window) and archive (filesystem or S3/MinIO) are swappable adapters. Old reservations can be archived and purged by a separate retention worker.

**Test:** `dotnet test tests/Mize.Reservations.UnitTests` (no Docker) · `dotnet test tests/Mize.Reservations.IntegrationTests` (needs Docker for Redis) · S3 tests run when `MIZE_S3_ENDPOINT`, `MIZE_S3_ACCESS_KEY` and `MIZE_S3_SECRET_KEY` point at MinIO.
**Try it:** `tools/throttle-test.ps1` checks the rate limit; `tools/try-flow.sh` walks every behaviour.

Full design, configuration, metrics, retention and measurements: [docs/DESIGN.md](docs/DESIGN.md).
