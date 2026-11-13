using System.Security.Cryptography;

namespace Winche.Storage.Infrastructure;

/// <summary>
/// Archive keys for uploads. Each upload gets a key of its own, made once and never reused: the file's folder, a random
/// generation, then its name (<c>users/u1/7f3a9c2e41b05d68/photo.jpg</c>). The name stays last, so the extension (and
/// with it a download's type) survives; the generation means a replaced, deleted or recreated file never shares bytes
/// with what came before, and an old upload link writes only to a key nothing reads.
/// </summary>
internal static class ObjectKeys
{
    public static string New(string path)
    {
        var generation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var slash = path.LastIndexOf('/');
        return slash < 0 ? $"{generation}/{path}" : $"{path[..slash]}/{generation}/{path[(slash + 1)..]}";
    }
}
