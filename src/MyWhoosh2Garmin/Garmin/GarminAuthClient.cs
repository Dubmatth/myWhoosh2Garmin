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

    public async Task<string> LoginAndGetTicketAsync(string email, string password, CancellationToken cancellationToken)
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

        string loginUrl =
            $"https://sso.garmin.com/mobile/api/login?clientId={ClientId}&locale=en-US&service={Uri.EscapeDataString(ServiceUrl)}";

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

        string responseType = body.RootElement
            .GetProperty("responseStatus")
            .GetProperty("type")
            .GetString() ?? "UNKNOWN";

        if (responseType != "SUCCESSFUL")
        {
            string message = body.RootElement.TryGetProperty("responseStatus", out var status)
                              && status.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? responseType
                : responseType;
            throw new GarminSsoException($"Échec de connexion Garmin : {message}");
        }

        return body.RootElement.GetProperty("serviceTicketId").GetString()
            ?? throw new GarminSsoException("Aucun ticket de service dans la réponse SSO Garmin.");
    }
}
