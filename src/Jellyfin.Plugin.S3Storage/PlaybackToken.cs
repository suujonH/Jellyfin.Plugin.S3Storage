using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.S3Storage;

public static class PlaybackToken
{
    public static string Create(string prefix, DateTimeOffset expiresAt, string secret)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, prefix, exp = expiresAt.ToUnixTimeSeconds() }));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(payload));
        return payload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
