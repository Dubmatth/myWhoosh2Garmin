using Microsoft.Extensions.Logging;
using MyWhoosh2Garmin.Garmin;
using MyWhoosh2Garmin.Notifications;
using MyWhoosh2Garmin.Tracking;
using MyWhoosh2Garmin.Utilities;

namespace MyWhoosh2Garmin;

public class SyncPipeline
{
    private readonly IActivityTracker _tracker;
    private readonly ITokenStore _tokenStore;
    private readonly GarminAuthClient _authClient;
    private readonly GarminUploadClient _uploadClient;
    private readonly IMacNotifier _notifier;
    private readonly ILogger<SyncPipeline> _logger;
    private readonly TimeProvider _timeProvider;
    private OAuth2Token? _cachedAccessToken;

    public SyncPipeline(
        IActivityTracker tracker,
        ITokenStore tokenStore,
        GarminAuthClient authClient,
        GarminUploadClient uploadClient,
        IMacNotifier notifier,
        ILogger<SyncPipeline> logger,
        TimeProvider? timeProvider = null)
    {
        _tracker = tracker;
        _tokenStore = tokenStore;
        _authClient = authClient;
        _uploadClient = uploadClient;
        _notifier = notifier;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleNewActivityAsync(string filePath, CancellationToken cancellationToken)
    {
        string hash = FileHasher.ComputeSha256(filePath);

        if (await _tracker.IsProcessedAsync(hash))
        {
            _logger.LogInformation("Activité déjà synchronisée, ignorée : {File}", filePath);
            return;
        }

        OAuth1Token? oauth1 = await _tokenStore.LoadOAuth1TokenAsync();
        if (oauth1 is null)
        {
            _logger.LogError("Aucun compte Garmin lié. Lance la commande `login` d'abord.");
            return;
        }

        if (_cachedAccessToken is null || _cachedAccessToken.IsExpired(_timeProvider))
        {
            _cachedAccessToken = await _authClient.ExchangeForAccessTokenAsync(oauth1, cancellationToken);
        }

        UploadResult result = await _uploadClient.UploadFitFileAsync(filePath, _cachedAccessToken.AccessToken, cancellationToken);

        if (result.Outcome is UploadOutcome.Uploaded or UploadOutcome.Duplicate)
        {
            await _tracker.MarkProcessedAsync(hash, Path.GetFileName(filePath), result.GarminActivityId);
            await _notifier.NotifyAsync("MyWhoosh → Garmin", "Activité synchronisée ✅");
        }
        else
        {
            _logger.LogError("Échec de l'upload vers Garmin pour {File} : {Error}", filePath, result.ErrorMessage);
        }
    }
}
