using Axon.Infrastructure.Security;

namespace Axon.UI.Application;

/// <summary>What start-up does before it opens the vault.</summary>
internal enum StartupEraseAction
{
    /// <summary>Nothing to resolve: open the app as usual.</summary>
    OpenNormally,

    /// <summary>An erase did not finish and the data can still be opened: ask the person to
    /// erase now or to cancel the erase and open with the data.</summary>
    AskAgain,

    /// <summary>An erase did not finish and the database key no longer works: finish it
    /// without asking, then tell the person it is done.</summary>
    FinishErase,

    /// <summary>No erase is pending, but the database key no longer works, so the stored data
    /// cannot be opened: offer to erase what is left and start fresh, or to close.</summary>
    KeyUnusable,

    /// <summary>The key store cannot be read right now: touch nothing; offer to try again or to close.</summary>
    KeyUnreadable,
}

internal static class StartupErase
{
    /// <summary>
    /// Decides from four facts:
    ///   • whether an erase left its marker;
    ///   • the state of the database key (<see cref="VaultKeyState"/>). An erase deletes nothing
    ///     while that key still unprotects, so a usable key means the data is still there;
    ///   • whether the vault is hardware-backed. With the development key store, keys are
    ///     derived from fixed names, so "the key is gone" proves nothing and the person is
    ///     always asked;
    ///   • whether a database file is present (a missing key with no database is a first run).
    /// An unreadable key store is never taken for a missing key: start-up then deletes
    /// nothing and opens nothing.
    /// </summary>
    public static StartupEraseAction Decide(
        bool markerPresent, VaultKeyState databaseKey, bool hardwareVault, bool databasePresent)
    {
        if (databaseKey == VaultKeyState.Unknown)
            return StartupEraseAction.KeyUnreadable;

        bool keyWorks = databaseKey == VaultKeyState.Usable;
        if (markerPresent)
        {
            return hardwareVault && !keyWorks
                ? StartupEraseAction.FinishErase
                : StartupEraseAction.AskAgain;
        }

        bool dataUnreadable = hardwareVault
            && (databaseKey == VaultKeyState.Unusable || (databaseKey == VaultKeyState.Missing && databasePresent));
        return dataUnreadable ? StartupEraseAction.KeyUnusable : StartupEraseAction.OpenNormally;
    }
}
