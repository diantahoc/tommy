using System.Security.Cryptography;
using System.Text;

namespace Tommy.Persistence.Storage;

/// <summary>
/// Content-addressed blob store under a chat (<c>blobs/&lt;sha256&gt;</c>). Writing the
/// same bytes twice dedupes; events reference blobs by hash so the log stays light.
/// </summary>
public sealed class BlobStore
{
    private readonly string _dir;

    public BlobStore(string dir) => _dir = dir;

    public string Put(byte[] data)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, hash);
        if (!File.Exists(path))
            File.WriteAllBytes(path, data);
        return hash;
    }

    public string PutText(string text) => Put(Encoding.UTF8.GetBytes(text));

    public byte[]? Get(string hash)
    {
        var path = Path.Combine(_dir, hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public string? GetText(string hash)
    {
        var bytes = Get(hash);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public bool Exists(string hash) => File.Exists(Path.Combine(_dir, hash));

    public string PathFor(string hash) => Path.Combine(_dir, hash);
}
