using System.Security.Cryptography;

namespace Axon.Infrastructure.Security;

/// <summary>
/// The AES-256-GCM envelope Axon persists: <c>nonce[12] || tag[16] || ciphertext</c>.
///
/// Used by <c>EncryptionDecorator</c> for field-level encryption in the database
/// (stored as Base64) and by <see cref="EncryptedFileOAuthTokenStore"/> for the
/// OAuth token files (stored as raw bytes). Both are on disk, so changing this
/// layout makes existing data unreadable. Callers own key derivation and lifetime.
/// </summary>
internal static class AesGcmEnvelope
{
    /// <summary>GCM nonce length (<c>AesGcm.NonceByteSizes.MaxSize</c>).</summary>
    public const int NonceSize = 12;

    /// <summary>GCM tag length (<c>AesGcm.TagByteSizes.MaxSize</c>).</summary>
    public const int TagSize = 16;

    private const int HeaderSize = NonceSize + TagSize;

    /// <summary>Encrypts under a fresh random nonce and returns <c>nonce || tag || ciphertext</c>.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        var blob = new byte[HeaderSize + plaintext.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ciphertext = blob.AsSpan(HeaderSize);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return blob;
    }

    /// <summary>
    /// Decrypts <c>nonce || tag || ciphertext</c>. Throws <see cref="ArgumentOutOfRangeException"/>
    /// when the blob is shorter than nonce + tag, and <see cref="AuthenticationTagMismatchException"/>
    /// when it was altered or sealed under another key.
    /// </summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob)
    {
        var nonce = blob.Slice(0, NonceSize);
        var tag = blob.Slice(NonceSize, TagSize);
        var ciphertext = blob.Slice(HeaderSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
