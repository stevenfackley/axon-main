using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SQLitePCL;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Provides a deterministic encrypted SQLite context for EF Core tooling.
/// </summary>
public sealed class AxonDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AxonDbContext>
{
    private static ReadOnlySpan<byte> DesignTimeKey =>
    [
        0x41, 0x78, 0x6f, 0x6e, 0x2d, 0x45, 0x46, 0x2d,
        0x44, 0x65, 0x73, 0x69, 0x67, 0x6e, 0x2d, 0x54,
        0x69, 0x6d, 0x65, 0x2d, 0x4b, 0x65, 0x79, 0x2d,
        0x4e, 0x65, 0x74, 0x31, 0x30, 0x2d, 0x45, 0x46
    ];

    public AxonDbContext CreateDbContext(string[] args)
    {
        Batteries_V2.Init();

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, ".ef");
        Directory.CreateDirectory(dataDirectory);

        var connectionString = AxonDbContextFactory.BuildConnectionString(dataDirectory, DesignTimeKey);

        return new AxonDbContext(AxonDbContextFactory.CreateOptions(connectionString));
    }
}
