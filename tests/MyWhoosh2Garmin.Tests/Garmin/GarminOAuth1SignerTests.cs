using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminOAuth1SignerTests
{
    [Fact]
    public void Signature_matches_independently_computed_vector_with_token()
    {
        string header = GarminOAuth1Signer.BuildAuthorizationHeader(
            httpMethod: "GET",
            baseUrl: "https://example.com/resource",
            queryOrBodyParams: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            consumerKey: "consumerkey",
            consumerSecret: "consumersecret",
            token: "token",
            tokenSecret: "tokensecret",
            nonce: "abc123nonce",
            unixTimestamp: 1700000000);

        Assert.Contains("oauth_signature=\"1%2Bm8vOz4ay%2BmFMO3SmhjPTA0IoE%3D\"", header);
    }

    [Fact]
    public void Signature_matches_independently_computed_vector_without_token()
    {
        string header = GarminOAuth1Signer.BuildAuthorizationHeader(
            httpMethod: "GET",
            baseUrl: "https://connectapi.garmin.com/oauth-service/oauth/preauthorized",
            queryOrBodyParams: new Dictionary<string, string>
            {
                ["ticket"] = "ST-123456",
                ["login-url"] = "https://mobile.integration.garmin.com/gcm/android",
                ["accepts-mfa-tokens"] = "true",
            },
            consumerKey: "consumerkey",
            consumerSecret: "consumersecret",
            token: null,
            tokenSecret: null,
            nonce: "noncevalue",
            unixTimestamp: 1700000000);

        Assert.Contains("oauth_signature=\"qUX5XcXCiAcJj2hjMuBllqBtVbQ%3D\"", header);
    }
}
