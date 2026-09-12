using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminAuthClientLoginTests
{
    [Fact]
    public async Task LoginAndGetTicketAsync_returns_ticket_on_successful_response()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mobile/api/login")
            {
                string json = """
                    { "responseStatus": { "type": "SUCCESSFUL" }, "serviceTicketId": "ST-abc-123" }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        string ticket = await client.LoginAndGetTicketAsync("me@example.com", "hunter2", CancellationToken.None);

        Assert.Equal("ST-abc-123", ticket);
    }

    [Fact]
    public async Task LoginAndGetTicketAsync_throws_on_failed_response()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            string json = """
                { "responseStatus": { "type": "FAILED", "message": "invalid credentials" } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        await Assert.ThrowsAsync<GarminSsoException>(() =>
            client.LoginAndGetTicketAsync("me@example.com", "wrong", CancellationToken.None));
    }
}
