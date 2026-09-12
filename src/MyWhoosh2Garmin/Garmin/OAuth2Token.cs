namespace MyWhoosh2Garmin.Garmin;

public record OAuth2Token(string AccessToken, string TokenType, DateTimeOffset ExpiresAt)
{
    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
}
