using System.Security.Cryptography;
using System.Text;

namespace MyWhoosh2Garmin.Garmin;

public static class GarminOAuth1Signer
{
    public static string BuildAuthorizationHeader(
        string httpMethod,
        string baseUrl,
        IReadOnlyDictionary<string, string> queryOrBodyParams,
        string consumerKey,
        string consumerSecret,
        string? token,
        string? tokenSecret,
        string nonce,
        long unixTimestamp)
    {
        var oauthParams = new Dictionary<string, string>
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = unixTimestamp.ToString(),
            ["oauth_version"] = "1.0",
        };
        if (!string.IsNullOrEmpty(token))
            oauthParams["oauth_token"] = token;

        var allParams = new Dictionary<string, string>(oauthParams);
        foreach (var kv in queryOrBodyParams)
            allParams[kv.Key] = kv.Value;

        string paramString = string.Join("&",
            allParams
                .Select(kv => $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}")
                .OrderBy(s => s, StringComparer.Ordinal));

        string baseString = string.Join("&",
            httpMethod.ToUpperInvariant(),
            PercentEncode(baseUrl),
            PercentEncode(paramString));

        string signingKey = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? "")}";

        byte[] hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString));
        oauthParams["oauth_signature"] = Convert.ToBase64String(hash);

        string headerParams = string.Join(", ",
            oauthParams
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{PercentEncode(kv.Key)}=\"{PercentEncode(kv.Value)}\""));

        return $"OAuth {headerParams}";
    }

    public static string PercentEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            bool isUnreserved =
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c is '-' or '.' or '_' or '~';

            if (isUnreserved)
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
