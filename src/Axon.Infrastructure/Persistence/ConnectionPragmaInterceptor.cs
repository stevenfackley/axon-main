using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Applies the vault's per-connection SQLite pragmas each time EF Core opens a connection.
/// SQLite keeps these settings on the connection, not in the file, so without this a second
/// connection (two operations at once, or a pooled connection being replaced) would run with
/// SQLite's defaults: a 2 MB cache and <c>synchronous=FULL</c>.
/// </summary>
internal sealed class ConnectionPragmaInterceptor : DbConnectionInterceptor
{
    public static readonly ConnectionPragmaInterceptor Instance = new();

    private const string Pragmas =
        "PRAGMA cache_size=-32000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Pragmas;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
