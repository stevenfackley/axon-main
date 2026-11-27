namespace Axon.Infrastructure.Security;

/// <summary>Whether a vault key can still be used, as far as the vault can tell.</summary>
public enum VaultKeyState
{
    /// <summary>No key is stored under the label.</summary>
    Missing,

    /// <summary>The key is there and unprotects: data encrypted under it can be opened.</summary>
    Usable,

    /// <summary>Something is stored, but it is empty or no longer unprotects (for example a
    /// blob that was zeroed by an erase whose delete then failed).</summary>
    Unusable,

    /// <summary>The key store could not be read (access denied, locked): neither usable nor gone.</summary>
    Unknown,
}

/// <summary>A vault that can say whether a key it stores still works, without handing it out.</summary>
public interface IVaultKeyInspector
{
    VaultKeyState GetKeyState(string keyLabel);
}
