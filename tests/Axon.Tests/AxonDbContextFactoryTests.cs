using Axon.Infrastructure.Persistence;

namespace Axon.Tests;

public class AxonDbContextFactoryTests
{
    [Fact]
    public void BuildConnectionString_UsesExpectedDatabasePathAndHexPassword()
    {
        byte[] key =
        [
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
            0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80,
            0x90, 0xa0, 0xb0, 0xc0, 0xd0, 0xe0, 0xf0, 0x12
        ];

        var connectionString = AxonDbContextFactory.BuildConnectionString(@"C:\data\axon", key);

        Assert.Equal(
            "Data Source=C:\\data\\axon\\axon.vault.db;Password=hex:00112233445566778899aabbccddeeff102030405060708090a0b0c0d0e0f012;",
            connectionString);
    }

    [Fact]
    public void CreateOptions_ConfiguresSqliteProvider()
    {
        var options = AxonDbContextFactory.CreateOptions("Data Source=:memory:;Password=hex:00;");
        using var context = new AxonDbContext(options);

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
    }
}
