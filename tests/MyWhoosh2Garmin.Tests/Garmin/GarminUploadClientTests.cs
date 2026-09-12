using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminUploadClientTests
{
    private static string CreateTempFitFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MyNewActivity-{Guid.NewGuid():N}.fit");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_uploaded_with_activity_id_on_success()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            string json = """
                { "detailedImportResult": { "successes": [ { "internalId": 999 } ], "failures": [] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Uploaded, result.Outcome);
        Assert.Equal("999", result.GarminActivityId);
        File.Delete(filePath);
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_duplicate_when_garmin_reports_code_202()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "detailedImportResult": { "successes": [], "failures": [ { "code": 202, "message": "Duplicate" } ] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Duplicate, result.Outcome);
        File.Delete(filePath);
    }

    [Fact]
    public async Task UploadFitFileAsync_returns_failed_with_message_on_other_error()
    {
        string filePath = CreateTempFitFile();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "detailedImportResult": { "successes": [], "failures": [ { "code": 500, "message": "Corrupt file" } ] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminUploadClient(new HttpClient(handler));
        UploadResult result = await client.UploadFitFileAsync(filePath, "access-token", CancellationToken.None);

        Assert.Equal(UploadOutcome.Failed, result.Outcome);
        Assert.Equal("Corrupt file", result.ErrorMessage);
        File.Delete(filePath);
    }
}
