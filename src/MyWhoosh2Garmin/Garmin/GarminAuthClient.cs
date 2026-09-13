using System.Net.Http.Json;
using System.Text.Json;

namespace MyWhoosh2Garmin.Garmin;

public class GarminAuthClient
{
    private const string ClientId = "GCM_ANDROID_DARK";
    private static readonly string ServiceUrl = "https://mobile.integration.garmin.com/gcm/android";

    private readonly HttpClient _httpClient;

    public GarminAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> LoginAndGetTicketAsync(
        string email, string password, Func<Task<string>> promptMfaCode, CancellationToken cancellationToken)
    {
        // Sets SSO cookies used by the following request; failures here are non-fatal.
        try
        {
            using var signInPage = new HttpRequestMessage(
                HttpMethod.Get, $"https://sso.garmin.com/mobile/sso/en/sign-in?clientId={ClientId}");
            await _httpClient.SendAsync(signInPage, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Best-effort; the login POST below still works without it in most cases.
        }

        string loginParams = $"clientId={ClientId}&locale=en-US&service={Uri.EscapeDataString(ServiceUrl)}";
        string loginUrl = $"https://sso.garmin.com/mobile/api/login?{loginParams}";

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = JsonContent.Create(new
            {
                username = email,
                password,
                rememberMe = false,
                captchaToken = "",
            }),
        };

        using var response = await _httpClient.SendAsync(loginRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        JsonDocument body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new GarminSsoException("Réponse SSO Garmin vide.");

        string responseType = body.RootElement.GetProperty("responseStatus").GetProperty("type").GetString() ?? "UNKNOWN";

        if (responseType == "SUCCESSFUL")
            return ExtractServiceTicket(body);

        if (responseType == "MFA_REQUIRED")
            return await CompleteMfaChallengeAsync(body, loginParams, promptMfaCode, cancellationToken);

        throw new GarminSsoException($"Échec de connexion Garmin : {ExtractStatusMessage(body, responseType)}");
    }

    private async Task<string> CompleteMfaChallengeAsync(
        JsonDocument loginResponseBody, string loginParams, Func<Task<string>> promptMfaCode, CancellationToken cancellationToken)
    {
        string mfaMethod = "email";
        if (loginResponseBody.RootElement.TryGetProperty("customerMfaInfo", out var mfaInfo)
            && mfaInfo.TryGetProperty("mfaLastMethodUsed", out var methodProp)
            && methodProp.GetString() is { } method)
        {
            mfaMethod = method;
        }

        string code = await promptMfaCode();

        string verifyUrl = $"https://sso.garmin.com/mobile/api/mfa/verifyCode?{loginParams}";
        using var verifyRequest = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
        {
            Content = JsonContent.Create(new
            {
                mfaMethod,
                mfaVerificationCode = code,
                rememberMyBrowser = false,
                reconsentList = Array.Empty<string>(),
                mfaSetup = false,
            }),
        };

        using var verifyResponse = await _httpClient.SendAsync(verifyRequest, cancellationToken);
        verifyResponse.EnsureSuccessStatusCode();

        JsonDocument verifyBody = await verifyResponse.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new GarminSsoException("Réponse de vérification MFA Garmin vide.");

        string verifyType = verifyBody.RootElement.GetProperty("responseStatus").GetProperty("type").GetString() ?? "UNKNOWN";
        if (verifyType != "SUCCESSFUL")
            throw new GarminSsoException($"Échec de vérification du code MFA : {ExtractStatusMessage(verifyBody, verifyType)}");

        return ExtractServiceTicket(verifyBody);
    }

    private static string ExtractStatusMessage(JsonDocument body, string fallback)
    {
        return body.RootElement.TryGetProperty("responseStatus", out var status)
                          && status.TryGetProperty("message", out var msg)
                          && !string.IsNullOrEmpty(msg.GetString())
            ? msg.GetString()!
            : fallback;
    }

    private static string ExtractServiceTicket(JsonDocument body)
    {
        return body.RootElement.GetProperty("serviceTicketId").GetString()
            ?? throw new GarminSsoException("Aucun ticket de service dans la réponse SSO Garmin.");
    }

    private const string OAuthConsumerUrl = "https://thegarth.s3.amazonaws.com/oauth_consumer.json";
    private (string Key, string Secret)? _cachedConsumer;

    private async Task<(string Key, string Secret)> GetOAuthConsumerAsync(CancellationToken cancellationToken)
    {
        if (_cachedConsumer is not null)
            return _cachedConsumer.Value;

        using var response = await _httpClient.GetAsync(OAuthConsumerUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        _cachedConsumer = (json.GetProperty("consumer_key").GetString()!, json.GetProperty("consumer_secret").GetString()!);
        return _cachedConsumer.Value;
    }

    public async Task<OAuth1Token> GetOAuth1TokenAsync(string ticket, CancellationToken cancellationToken)
    {
        var (consumerKey, consumerSecret) = await GetOAuthConsumerAsync(cancellationToken);

        string baseUrl = "https://connectapi.garmin.com/oauth-service/oauth/preauthorized";
        var queryParams = new Dictionary<string, string>
        {
            ["ticket"] = ticket,
            ["login-url"] = ServiceUrl,
            ["accepts-mfa-tokens"] = "true",
        };

        string authHeader = GarminOAuth1Signer.BuildAuthorizationHeader(
            "GET", baseUrl, queryParams, consumerKey, consumerSecret,
            token: null, tokenSecret: null,
            nonce: Guid.NewGuid().ToString("N"),
            unixTimestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        string url = baseUrl + "?" + string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        request.Headers.UserAgent.ParseAdd("com.garmin.android.apps.connectmobile");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        string body2 = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = System.Web.HttpUtility.ParseQueryString(body2);
        string? oauthToken = parsed["oauth_token"];
        string? oauthTokenSecret = parsed["oauth_token_secret"];
        if (oauthToken is null || oauthTokenSecret is null)
            throw new GarminSsoException("Réponse OAuth1 Garmin incomplète.");

        return new OAuth1Token(oauthToken, oauthTokenSecret);
    }

    public async Task<OAuth2Token> ExchangeForAccessTokenAsync(OAuth1Token oauth1, CancellationToken cancellationToken)
    {
        var (consumerKey, consumerSecret) = await GetOAuthConsumerAsync(cancellationToken);

        string baseUrl = "https://connectapi.garmin.com/oauth-service/oauth/exchange/user/2.0";
        var formParams = new Dictionary<string, string> { ["audience"] = "GARMIN_CONNECT_MOBILE_ANDROID_DI" };

        string authHeader = GarminOAuth1Signer.BuildAuthorizationHeader(
            "POST", baseUrl, formParams, consumerKey, consumerSecret,
            oauth1.OAuthToken, oauth1.OAuthTokenSecret,
            nonce: Guid.NewGuid().ToString("N"),
            unixTimestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new FormUrlEncodedContent(formParams),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        request.Headers.UserAgent.ParseAdd("com.garmin.android.apps.connectmobile");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        string accessToken = json.GetProperty("access_token").GetString()!;
        string tokenType = json.GetProperty("token_type").GetString()!;
        int expiresIn = json.GetProperty("expires_in").GetInt32();

        return new OAuth2Token(accessToken, tokenType, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}
