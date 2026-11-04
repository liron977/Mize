using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mize.Reservations.Core.Abstractions;

namespace Mize.Reservations.Archive.FileSystem;

public static class FileSystemArchiveServiceCollectionExtensions
{
    /// <summary>Registers the directory-backed archive. Configuration (read lazily): <c>Archive:FileSystem:Path</c> (default <c>archive</c>).</summary>
    public static IServiceCollection AddFileSystemArchive(this IServiceCollection services)
    {
        services.AddSingleton<IReservationArchive>(sp =>
            new FileSystemReservationArchive(sp.GetRequiredService<IConfiguration>()["Archive:FileSystem:Path"] ?? "archive"));
        return services;
    }
}
