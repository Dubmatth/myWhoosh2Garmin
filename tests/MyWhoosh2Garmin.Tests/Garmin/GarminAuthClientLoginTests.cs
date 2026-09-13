using System.Net;
using System.Text;
using MyWhoosh2Garmin.Garmin;

namespace MyWhoosh2Garmin.Tests.Garmin;

public class GarminAuthClientLoginTests
{
    private static Task<string> NeverPromptForMfa() =>
        throw new InvalidOperationException("MFA prompt should not have been called for this test.");

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

        string ticket = await client.LoginAndGetTicketAsync(
            "me@example.com", "hunter2", NeverPromptForMfa, CancellationToken.None);

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
            client.LoginAndGetTicketAsync("me@example.com", "wrong", NeverPromptForMfa, CancellationToken.None));
    }

    [Fact]
    public async Task LoginAndGetTicketAsync_completes_mfa_challenge_and_returns_ticket()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mobile/api/login")
            {
                string json = """
                    {
                        "responseStatus": { "type": "MFA_REQUIRED", "message": "" },
                        "customerMfaInfo": { "mfaLastMethodUsed": "email" }
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            if (request.RequestUri!.AbsolutePath == "/mobile/api/mfa/verifyCode")
            {
                string json = """
                    { "responseStatus": { "type": "SUCCESSFUL" }, "serviceTicketId": "ST-after-mfa" }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        string ticket = await client.LoginAndGetTicketAsync(
            "me@example.com", "hunter2", () => Task.FromResult("123456"), CancellationToken.None);

        Assert.Equal("ST-after-mfa", ticket);
    }

    [Fact]
    public async Task LoginAndGetTicketAsync_throws_when_mfa_code_is_rejected()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mobile/api/login")
            {
                string json = """
                    {
                        "responseStatus": { "type": "MFA_REQUIRED", "message": "" },
                        "customerMfaInfo": { "mfaLastMethodUsed": "email" }
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            string failJson = """
                { "responseStatus": { "type": "FAILED", "message": "code invalide" } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(failJson, Encoding.UTF8, "application/json"),
            };
        });

        var client = new GarminAuthClient(new HttpClient(handler));

        await Assert.ThrowsAsync<GarminSsoException>(() =>
            client.LoginAndGetTicketAsync(
                "me@example.com", "hunter2", () => Task.FromResult("000000"), CancellationToken.None));
    }
}
