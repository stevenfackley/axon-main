namespace Axon.UI.Application;

/// <summary>What start-up does when it finds the erase-pending marker.</summary>
internal enum StartupEraseAction
{
    /// <summary>No unfinished erase: open the app as usual.</summary>
    OpenNormally,

    /// <summary>Ask the person: erase now, or cancel the erase and open with the data.</summary>
    AskAgain,

    /// <summary>Finish the erase without asking, then tell the person it is done.</summary>
    FinishErase,
}

internal static class StartupErase
{
    /// <summary>
    /// An unfinished erase is finished without asking only when the database key is already
    /// gone: the database cannot be read then anyway, and the erase deletes nothing before
    /// that key is destroyed. While the key exists (the erase stopped before it, or a backup
    /// brought it back), nothing is deleted without the person's say. With the development
    /// key store, keys are derived from fixed names, so "the key is gone" proves nothing and
    /// the person is always asked.
    /// </summary>
    public static StartupEraseAction Decide(bool markerPresent, bool databaseKeyPresent, bool hardwareVault) =>
        !markerPresent ? StartupEraseAction.OpenNormally
        : hardwareVault && !databaseKeyPresent ? StartupEraseAction.FinishErase
        : StartupEraseAction.AskAgain;
}
