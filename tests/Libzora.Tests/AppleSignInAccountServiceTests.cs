using System.Net;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class AppleSignInAccountServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libzora-apple-auth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task SignInBindAndUnbindApple_UseExpectedEndpointsAndPersistState()
    {
        var userId = Guid.NewGuid();
        var token = new string('A', 43);
        var requests = new List<(string Path, string? Region, string? Bearer, string Body)>();

        using var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var region = request.Headers.TryGetValues("X-Libzora-Region", out var values) ? values.FirstOrDefault() : null;
            requests.Add((request.RequestUri!.AbsolutePath, region, request.Headers.Authorization?.Parameter, body));

            if (request.RequestUri.AbsolutePath.EndsWith("/api/accounts/apple/login", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Serialize(new AccountSessionResponse
                {
                    Region = AccountRegion.China,
                    User = new AccountUser(userId, "apple_reader", "reader@privaterelay.appleid.com", true, AppleLinked: true),
                    Token = token,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
                }, AccountJsonContext.Default.AccountSessionResponse);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/api/accounts/apple/unbind", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Serialize(
                    new AccountUser(userId, "apple_reader", "reader@privaterelay.appleid.com", true, AppleLinked: false),
                    AccountJsonContext.Default.AccountUser);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/api/accounts/apple/bind", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Serialize(
                    new AccountUser(userId, "apple_reader", "reader@privaterelay.appleid.com", true, AppleLinked: true),
                    AccountJsonContext.Default.AccountUser);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        using var service = new AccountService(new AppPaths(_root), new PassThroughProtector(), http);

        var signedIn = await service.SignInWithAppleAsync("https://accounts.example.test", new AppleSignInCredential
        {
            IdentityToken = "jwt.header.payload",
            AuthorizationCode = "code-123",
            Nonce = "nonce-xyz",
            Email = "reader@privaterelay.appleid.com",
            DisplayName = "Reader"
        });

        Assert.True(signedIn.IsLoggedIn);
        Assert.Equal("apple_reader", signedIn.User?.Username);
        Assert.True(signedIn.User?.AppleLinked);

        var unbound = await service.UnbindAppleAsync();
        Assert.False(unbound.User?.AppleLinked);

        var rebound = await service.BindAppleAsync(new AppleSignInCredential
        {
            IdentityToken = "jwt.header.payload.2",
            Nonce = "nonce-456"
        });
        Assert.True(rebound.User?.AppleLinked);

        Assert.Equal(3, requests.Count);
        Assert.Equal("/api/accounts/apple/login", requests[0].Path);
        Assert.Equal("china", requests[0].Region);
        Assert.Null(requests[0].Bearer);
        Assert.Contains("\"identityToken\":\"jwt.header.payload\"", requests[0].Body);

        Assert.Equal("/api/accounts/apple/unbind", requests[1].Path);
        Assert.Equal(token, requests[1].Bearer);

        Assert.Equal("/api/accounts/apple/bind", requests[2].Path);
        Assert.Equal(token, requests[2].Bearer);
        Assert.Contains("\"identityToken\":\"jwt.header.payload.2\"", requests[2].Body);
    }

    [Fact]
    public async Task SignInWithApple_ReturnsFriendlyMessageWhenServerNotUpdatedYet()
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        using var service = new AccountService(new AppPaths(_root), new PassThroughProtector(), http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignInWithAppleAsync("https://accounts.example.test", new AppleSignInCredential
            {
                IdentityToken = "jwt.token"
            }));

        Assert.Contains("Apple 登录", error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
