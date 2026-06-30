using System.Text;

namespace Winche.Storage.AspNetCore.Rest.Infrastructure;

/// <summary>
/// Encodes/decodes the <c>{path}</c> route segment as unpadded base64url (RFC 4648 §5,
/// <c>-</c>/<c>_</c> alphabet). Decoding restores the standard alphabet and re-adds the dropped
/// padding before <see cref="Convert.FromBase64String"/>.
/// </summary>
internal static class Base64UrlPath
{
    public static string Decode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    public static string Encode(string value)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
