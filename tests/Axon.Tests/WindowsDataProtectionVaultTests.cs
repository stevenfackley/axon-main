using System.Runtime.Versioning;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Tests for <see cref="WindowsDataProtectionVault"/>.
/// DPAPI is Windows-only; each test guards with <see cref="OperatingSystem.IsWindows()"/>
/// and skips gracefully on non-Windows CI agents.
///
/// Each test uses an isolated temp directory that is deleted in <see cref="Dispose"/>.
/// </summary>
public sealed class WindowsDataProtectionVaultTests : IDisposable
{
    private readonly string _tempDir;

    public WindowsDataProtectionVaultTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"axon-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private WindowsDataProtectionVault Vault() => new(_tempDir);

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two vault instances sharing the same directory must return identical
    /// key bytes for the same label (blob written by vault1, re-read by vault2).
    /// </summary>
    [Fact]
    public async Task DeriveKeyAsync_SameLabel_TwoInstances_ReturnIdenticalBytes()
    {
        if (!OperatingSystem.IsWindows()) return;   // skip on non-Windows CI

        var vault1 = Vault();
        var vault2 = Vault();

        var mem1 = await vault1.DeriveKeyAsync("axon.test.same");
        var mem2 = await vault2.DeriveKeyAsync("axon.test.same");

        try
        {
            Assert.Equal(mem1.Span.ToArray(), mem2.Span.ToArray());
        }
        finally
        {
            vault1.ZeroKey(mem1);
            vault2.ZeroKey(mem2);
        }
    }

    /// <summary>
    /// Different labels must yield different key bytes.
    /// </summary>
    [Fact]
    public async Task DeriveKeyAsync_DifferentLabels_ReturnDifferentBytes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vault = Vault();
        var memA = await vault.DeriveKeyAsync("axon.test.label-a");
        var memB = await vault.DeriveKeyAsync("axon.test.label-b");

        try
        {
            Assert.False(memA.Span.SequenceEqual(memB.Span),
                "Different labels must produce different key material.");
        }
        finally
        {
            vault.ZeroKey(memA);
            vault.ZeroKey(memB);
        }
    }

    /// <summary>
    /// Returned key must be exactly 32 bytes (AES-256).
    /// </summary>
    [Fact]
    public async Task DeriveKeyAsync_Returns32Bytes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vault = Vault();
        var mem = await vault.DeriveKeyAsync("axon.test.length");
        try
        {
            Assert.Equal(32, mem.Length);
        }
        finally
        {
            vault.ZeroKey(mem);
        }
    }

    /// <summary>
    /// After <see cref="WindowsDataProtectionVault.DestroyKeyAsync"/> the blob
    /// file must not exist and a subsequent derive must return a fresh, different key.
    /// </summary>
    [Fact]
    public async Task DestroyKeyAsync_RemovesBlobAndNextDeriveDiffersFromOriginal()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string label = "axon.test.destroy";
        var vault = Vault();

        var original = await vault.DeriveKeyAsync(label);
        var originalBytes = original.Span.ToArray();
        vault.ZeroKey(original);

        await vault.DestroyKeyAsync(label);

        // Blob must be absent from disk.
        var remaining = Directory.GetFiles(_tempDir, "*.key");
        Assert.Empty(remaining);

        // Re-derive must generate fresh (different) key.
        var fresh = await vault.DeriveKeyAsync(label);
        try
        {
            Assert.False(fresh.Span.SequenceEqual(originalBytes),
                "Key re-derived after destroy must differ from the destroyed key.");
        }
        finally
        {
            vault.ZeroKey(fresh);
        }
    }

    /// <summary>
    /// <see cref="WindowsDataProtectionVault.ZeroKey"/> must overwrite the
    /// returned buffer with zeros.
    /// </summary>
    [Fact]
    public async Task ZeroKey_OverwritesBufferWithZeros()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vault = Vault();
        var mem = await vault.DeriveKeyAsync("axon.test.zero");

        // At least one non-zero byte must exist before zeroing.
        Assert.True(mem.Span.ToArray().Any(b => b != 0),
            "A freshly derived key should not be all zeros.");

        vault.ZeroKey(mem);

        Assert.True(mem.Span.ToArray().All(b => b == 0),
            "All bytes must be zero after ZeroKey.");
    }

    /// <summary>
    /// <see cref="WindowsDataProtectionVault.IsHardwareBacked"/> must return
    /// <see langword="true"/> on Windows (DPAPI is OS-backed).
    /// </summary>
    [Fact]
    public void IsHardwareBacked_TrueOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vault = Vault();
        Assert.True(vault.IsHardwareBacked);
    }
}
