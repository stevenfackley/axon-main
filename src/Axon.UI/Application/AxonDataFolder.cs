using System.Text.RegularExpressions;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Decorators;
using Axon.Infrastructure.Security;

namespace Axon.UI.Application;

/// <summary>
/// Everything Axon itself writes in its data folder (<c>%LOCALAPPDATA%\Axon</c> on Windows),
/// by name: the one list the data erase works from. Each writer takes its name from here
/// or from the constant this list reads, so the two cannot drift apart.
///
/// The app keeps no settings file: the air-gap and telemetry toggles, the Whoop sync
/// watermark and the licence tier live in memory or in environment variables.
/// </summary>
internal static class AxonDataFolder
{
    /// <summary>DPAPI key blobs (<see cref="WindowsDataProtectionVault"/>).</summary>
    public const string KeysFolder = "keys";

    /// <summary>Encrypted OAuth tokens (<see cref="EncryptedFileOAuthTokenStore"/>).</summary>
    public const string TokensFolder = "tokens";

    /// <summary>The rolling application log (Serilog, desktop only).</summary>
    public const string LogsFolder = "logs";

    /// <summary>Serilog's rolling-file template: <c>axon-.log</c> becomes <c>axon-20261127.log</c>,
    /// then <c>axon-20261127_001.log</c> once a day's file reaches its size limit.</summary>
    public const string LogFileTemplate = "axon-.log";

    /// <summary>The relay health report (JsonHealthReportWriter).</summary>
    public const string HealthFolder = "health";

    public const string HealthFileName = "status.json";

    /// <summary>Written when an erase starts; removed last, once nothing Axon wrote is left.</summary>
    public const string ErasePendingMarker = "erase-pending";

    /// <summary>Every vault key Axon derives.</summary>
    public static readonly IReadOnlyList<string> KeyLabels =
    [
        AxonDbContextFactory.KeyLabel,
        EncryptionDecorator.EncKeyLabel,
        EncryptedFileOAuthTokenStore.TokenKeyLabel,
    ];

    /// <summary>The database and the files SQLite keeps beside it, at the top of the folder.</summary>
    public static readonly IReadOnlyList<string> DatabaseFiles =
    [
        AxonDbContextFactory.DbFileName,
        AxonDbContextFactory.DbFileName + "-wal",
        AxonDbContextFactory.DbFileName + "-shm",
        AxonDbContextFactory.DbFileName + "-journal",
    ];

    /// <summary>The key blob files, one per label.</summary>
    public static IEnumerable<string> KeyFiles => KeyLabels.Select(WindowsDataProtectionVault.KeyFileName);

    /// <summary>True for a file name inside <see cref="TokensFolder"/> that the token store writes.</summary>
    public static bool IsTokenFile(string name) =>
        name.EndsWith(EncryptedFileOAuthTokenStore.TokenFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a file name inside <see cref="LogsFolder"/> that the rolling log writes.</summary>
    public static bool IsLogFile(string name) => LogFileName.IsMatch(name);

    // The template's stem + yyyyMMdd + optional "_NNN" + its extension.
    private static readonly Regex LogFileName = new(
        @"\A" + Regex.Escape(Path.GetFileNameWithoutExtension(LogFileTemplate)) + @"\d{8}(_\d{3,})?"
        + Regex.Escape(Path.GetExtension(LogFileTemplate)) + @"\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
