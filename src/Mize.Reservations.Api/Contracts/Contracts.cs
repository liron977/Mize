namespace Mize.Reservations.Api.Contracts;

public sealed record IngestResponse(string Status, bool Changed);

public sealed record StatsResponse(string SupplierId, long Ingested, long Throttled, long Unchanged, long Stale);
