using System.Security.Cryptography;
using System.Text;

namespace Blast;

internal sealed class ObjectStore
{
    internal static Action<string, string>? PublicationBoundaryForTest { get; set; }
    private const int MaxBytes = 8 * 1024 * 1024; // Current bounded in-memory implementation.
    private readonly string directory;
    private readonly byte[] key;

    internal ObjectStore(string stateDirectory, bool allowNewKey = true)
    {
        directory = Path.Combine(stateDirectory, "objects");
        Directory.CreateDirectory(directory);
        string keyPath = Path.Combine(stateDirectory, "object-key.dpapi");
        if (File.Exists(keyPath))
        {
            key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        }
        else
        {
            if (!allowNewKey || Directory.EnumerateFiles(directory).Any())
                throw new InvalidDataException("Object key is missing from a nonempty state store; evidence preserved.");
            key = RandomNumberGenerator.GetBytes(32);
            byte[] wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            stream.Write(wrapped);
            stream.Flush(true);
        }
        if (key.Length != 32) throw new InvalidDataException("Invalid object key length.");
    }

    internal static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    internal string Save(byte[] plaintext)
    {
        if (plaintext.Length > MaxBytes) throw new NotSupportedException($"File exceeds {MaxBytes} bytes.");
        string id = Hash(plaintext);
        string destination = Path.Combine(directory, id + ".bro");
        if (File.Exists(destination))
        {
            Verify(id, plaintext.Length);
            return id;
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        string temporary = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                PublicationBoundaryForTest?.Invoke("encrypted_temp_created", temporary);
                writer.Write(Encoding.ASCII.GetBytes("BR01"));
                writer.Write((long)plaintext.Length);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(ciphertext);
                writer.Flush();
                stream.Flush(true);
            }
            PublicationBoundaryForTest?.Invoke("encrypted_temp_flushed", temporary);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            Verify(id, plaintext.Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
        PublicationBoundaryForTest?.Invoke("new_object_published", destination);
        Verify(id, plaintext.Length);
        PublicationBoundaryForTest?.Invoke("new_object_verified", destination);
        return id;
    }

    internal byte[] Read(string id)
    {
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid object identifier.");
        string path = Path.Combine(directory, id + ".bro");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "BR01")
            throw new InvalidDataException("Invalid object header.");
        long size = reader.ReadInt64();
        if (size < 0 || size > MaxBytes || stream.Length != 4 + 8 + 12 + 16 + size)
            throw new InvalidDataException("Invalid object size.");
        byte[] nonce = reader.ReadBytes(12);
        byte[] tag = reader.ReadBytes(16);
        byte[] ciphertext = reader.ReadBytes((int)size);
        byte[] plaintext = new byte[size];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, ciphertext, tag, plaintext);
            if (!Hash(plaintext).Equals(id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Object content hash mismatch.");
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(ciphertext); }
    }

    internal void Verify(string id, long expectedLength)
    {
        byte[] data = Read(id);
        try
        {
            if (data.LongLength != expectedLength) throw new InvalidDataException("Object length mismatch.");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    internal string PathForTest(string id) => Path.Combine(directory, id + ".bro");
}
