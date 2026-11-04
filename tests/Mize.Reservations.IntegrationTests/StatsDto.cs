namespace Mize.Reservations.IntegrationTests;

/// <summary>The body of GET /api/reservations/stats/{supplierId}, shared by every API test.</summary>
public record StatsDto(string SupplierId, long Ingested, long Throttled, long Unchanged, long Stale);
