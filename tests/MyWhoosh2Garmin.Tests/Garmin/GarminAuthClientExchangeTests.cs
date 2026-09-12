using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminAuthClientExchangeTests
{
    private static HttpResponseMessage ConsumerKeyResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """{ "consumer_key": "fake-consumer-key", "consumer_secret": "fake-consumer-secret" }""",
            Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetOAuth1TokenAsync_parses_form_encoded_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("oauth_consumer.json"))
                return ConsumerKeyResponse();

            Assert.Contains("oauth-service/oauth/preauthorized", url);
            Assert.NotNull(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "oauth_token=tok-123&oauth_token_secret=secret-456", Encoding.UTF8,
                    "application/x-www-form-urlencoded"),
            };
        });
        var client = new GarminAuthClient(new HttpClient(handler));

        OAuth1Token token = await client.GetOAuth1TokenAsync("ST-abc-123", CancellationToken.None);

        Assert.Equal("tok-123", token.OAuthToken);
        Assert.Equal("secret-456", token.OAuthTokenSecret);
    }

    [Fact]
    public async Task ExchangeForAccessTokenAsync_parses_json_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("oauth_consumer.json"))
                return ConsumerKeyResponse();

            Assert.Contains("oauth-service/oauth/exchange/user/2.0", url);
            string json = """
                {
                    "access_token": "access-789",
                    "token_type": "Bearer",
                    "expires_in": 3600
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var client = new GarminAuthClient(new HttpClient(handler));
        var oauth1 = new OAuth1Token("tok-123", "secret-456");

        OAuth2Token token = await client.ExchangeForAccessTokenAsync(oauth1, CancellationToken.None);

        Assert.Equal("access-789", token.AccessToken);
        Assert.False(token.IsExpired(TimeProvider.System));
    }
}
