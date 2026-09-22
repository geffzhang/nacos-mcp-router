using System.Net;
using FluentAssertions;
using NacosMcpRouter.Mcp;

namespace NacosMcpRouter.Tests.Mcp;

public sealed class OAuthTokenServiceTests
{
    [Fact]
    public async Task ClientCredentials_ReusesUnexpiredToken()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/token");
            return OAuthTestResponse.Json("{\"access_token\":\"token-1\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        });
        using var httpClient = new HttpClient(handler);
        var service = new OAuthTokenService(httpClient);
        var options = new OAuthOptions(
            "client_credentials", "client", "secret", "https://idp.test/token", null, null,
            null, null, ["mcp.read"]);

        (await service.GetAccessTokenAsync(options)).Should().Be("token-1");
        (await service.GetAccessTokenAsync(options)).Should().Be("token-1");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task DeviceCodeWithoutCode_ReturnsAuthorizationInstructions()
    {
        var handler = new RecordingHandler((_, _) => OAuthTestResponse.Json(
            "{\"device_code\":\"device-1\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://idp.test/verify\",\"expires_in\":600,\"interval\":5}"));
        using var httpClient = new HttpClient(handler);
        var service = new OAuthTokenService(httpClient);
        var options = new OAuthOptions(
            "device_code", "client", null, "https://idp.test/token", null,
            "https://idp.test/device", null, null, []);

        var act = () => service.GetAccessTokenAsync(options).AsTask();
        var exception = await act.Should().ThrowAsync<OAuthAuthorizationRequiredException>();
        exception.Which.UserCode.Should().Be("ABCD");
        exception.Which.VerificationUri.Should().Be("https://idp.test/verify");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request, cancellationToken));
        }
    }

    private static class OAuthTestResponse
    {
        public static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}