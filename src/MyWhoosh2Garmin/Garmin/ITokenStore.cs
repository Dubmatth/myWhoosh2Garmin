namespace MyWhoosh2Garmin.Garmin;

public interface ITokenStore
{
    Task SaveOAuth1TokenAsync(OAuth1Token token);
    Task<OAuth1Token?> LoadOAuth1TokenAsync();
}
