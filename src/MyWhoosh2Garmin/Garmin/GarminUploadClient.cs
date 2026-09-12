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

        if (detailedResult.TryGetProperty("successes", out var successes) && successes.GetArrayLength() > 0)
        {
            string activityId = successes[0].GetProperty("internalId").ToString();
            return new UploadResult(UploadOutcome.Uploaded, activityId, null);
        }

        if (detailedResult.TryGetProperty("failures", out var failures) && failures.GetArrayLength() > 0)
        {
            var failure = failures[0];
            int code = failure.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;
            string message = failure.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";

            return code == DuplicateActivityCode
                ? new UploadResult(UploadOutcome.Duplicate, null, null)
                : new UploadResult(UploadOutcome.Failed, null, message);
        }

        return new UploadResult(UploadOutcome.Failed, null, $"Réponse Garmin inattendue : {body}");
    }
}
