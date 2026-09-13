using System.Net.Http.Headers;
using System.Text.Json;

namespace MyWhoosh2Garmin.Garmin;

public class GarminUploadClient
{
    private const int DuplicateActivityCode = 202;
    private readonly HttpClient _httpClient;

    public GarminUploadClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UploadResult> UploadFitFileAsync(string filePath, string accessToken, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        using var fileContent = new ByteArrayContent(fileBytes);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://connectapi.garmin.com/upload-service/upload/.fit")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("GCM-iOS-5.22.1.4");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(body);
        var detailedResult = doc.RootElement.GetProperty("detailedImportResult");

        if (detailedResult.TryGetProperty("failures", out var failures) && failures.GetArrayLength() > 0)
        {
            var failure = failures[0];
            int code = failure.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;
            string message = failure.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";

            return code == DuplicateActivityCode
                ? new UploadResult(UploadOutcome.Duplicate, null, null)
                : new UploadResult(UploadOutcome.Failed, null, message);
        }

        // No failures reported: verified against a real account that Garmin can accept the
        // file while leaving "successes" empty too — absence of a failure is the reliable
        // success signal, not a populated "successes" entry. Prefer the per-activity
        // internalId when present, falling back to the upload's own id otherwise.
        string? activityId = detailedResult.TryGetProperty("successes", out var successes) && successes.GetArrayLength() > 0
            ? successes[0].GetProperty("internalId").ToString()
            : detailedResult.TryGetProperty("uploadId", out var uploadIdProp)
                ? uploadIdProp.ToString()
                : null;

        return new UploadResult(UploadOutcome.Uploaded, activityId, null);
    }
}
