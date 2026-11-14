using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SpotifyToTidal;
using Xunit;

public sealed class OAuthManagerTests
{
    private sealed class FakeTokenServer : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Body;
        public int Calls;
        public string? LastForm;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Calls++;
            LastForm = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Assert.EndsWith("/token", request.RequestUri?.AbsolutePath);
            var dto = new OAuthTokenResponse
            {
                AccessToken = "issued-access", RefreshToken = "issued-refresh", ExpiresIn = 600
            };
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body ?? JsonSerializer.Serialize(dto,
                    AppJsonContext.Default.OAuthTokenResponse), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "SpotifyToTidal-OAuth-" + Guid.NewGuid().ToString("N"));

    private static int FreeLoopbackPort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static AppSettings Settings(int port = 8765) =>
        new("spotify-client", "tidal-client",
            $"http://127.0.0.1:{port}/callback/", $"http://127.0.0.1:{port}/callback/",
            "US");

    private static string QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == key) return Uri.UnescapeDataString(kv[1]);
        }
        throw new ArgumentException("Parameter missing: " + key);
    }

    [Fact]
    public async Task AccessToken_requires_existing_file()
    {
        var server = new FakeTokenServer();
        using var http = new HttpClient(server);
        var mgr = new OAuthManager(TemporaryPath(), Settings(), http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.AccessToken("spotify"));
    }

    [Fact]
    public async Task Unexpired_token_is_read_without_network_or_reflection()
    {
        string path = TemporaryPath();
        Directory.CreateDirectory(path);
        var token = new OAuthToken("cached-token", "refresh", DateTimeOffset.UtcNow.AddMinutes(20));
        await File.WriteAllTextAsync(Path.Combine(path, "spotify-token.json"),
            JsonSerializer.Serialize(token, AppJsonContext.Default.OAuthToken));
        var server = new FakeTokenServer();
        using var http = new HttpClient(server);
        Assert.Equal("cached-token", await new OAuthManager(path, Settings(), http).AccessToken("spotify"));
        Assert.Equal(0, server.Calls);
    }

    [Fact]
    public async Task Expired_token_without_refresh_is_rejected()
    {
        string path = TemporaryPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "tidal-token.json"),
            JsonSerializer.Serialize(new OAuthToken("old", null, DateTimeOffset.UtcNow.AddMinutes(-20)),
                AppJsonContext.Default.OAuthToken));
        using var http = new HttpClient(new FakeTokenServer());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthManager(path, Settings(), http).AccessToken("tidal"));
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("tidal")]
    public async Task Expired_token_is_refreshed_and_saved_with_generated_JSON(string service)
    {
        string path = TemporaryPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, service + "-token.json"),
            JsonSerializer.Serialize(new OAuthToken("expired", "old-refresh",
                DateTimeOffset.UtcNow.AddMinutes(-1)), AppJsonContext.Default.OAuthToken));
        var server = new FakeTokenServer();
        using var http = new HttpClient(server);
        var manager = new OAuthManager(path, Settings(), http);
        Assert.Equal("issued-access", await manager.AccessToken(service));
        Assert.Contains("grant_type=refresh_token", server.LastForm);
        Assert.Contains("old-refresh", server.LastForm);
        Assert.Equal(1, server.Calls);
        Assert.Equal("issued-access", await manager.AccessToken(service));
        Assert.Equal(1, server.Calls);
        var stored = JsonSerializer.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(path, service + "-token.json")), AppJsonContext.Default.OAuthToken);
        Assert.Equal("issued-refresh", stored?.RefreshToken);
    }

    [Fact]
    public async Task Token_endpoint_error_surfaces_service_failure()
    {
        string path = TemporaryPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "spotify-token.json"),
            JsonSerializer.Serialize(new OAuthToken("expired", "refresh", DateTimeOffset.UtcNow.AddSeconds(-1)),
                AppJsonContext.Default.OAuthToken));
        var server = new FakeTokenServer { Status = HttpStatusCode.BadRequest, Body = "bad token" };
        using var http = new HttpClient(server);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthManager(path, Settings(), http).AccessToken("spotify"));
        Assert.Contains("bad token", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REPLACE_SPOTIFY_CLIENT_ID")]
    public async Task Login_rejects_missing_or_unregistered_client_id(string clientId)
    {
        var settings = Settings() with { SpotifyClientId = clientId };
        using var http = new HttpClient(new FakeTokenServer());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthManager(TemporaryPath(), settings, http).Login("spotify"));
    }

    [Theory]
    [InlineData("https://127.0.0.1:12345/callback/")]
    [InlineData("http://localhost:12345/callback/")]
    [InlineData("http://127.0.0.1:12345/callback")]
    public async Task Login_requires_exact_loopback_callback(string redirect)
    {
        var settings = Settings() with { SpotifyRedirectUri = redirect };
        using var http = new HttpClient(new FakeTokenServer());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthManager(TemporaryPath(), settings, http).Login("spotify"));
    }

    [Theory]
    [InlineData("spotify", true, true)]
    [InlineData("tidal", true, true)]
    [InlineData("spotify", false, true)]
    [InlineData("spotify", true, false)]
    public async Task PKCE_loopback_verifies_state_code_and_exchanges_token(
        string service, bool correctState, bool provideCode)
    {
        int port = FreeLoopbackPort();
        string path = TemporaryPath();
        var settings = Settings(port);
        var server = new FakeTokenServer();
        using var http = new HttpClient(server);
        Task? callback = null;
        var manager = new OAuthManager(path, settings, http, url =>
        {
            var uri = new Uri(url);
            string state = QueryValue(uri, "state");
            Assert.Equal("S256", QueryValue(uri, "code_challenge_method"));
            Assert.Equal("code", QueryValue(uri, "response_type"));
            Assert.Equal(service == "spotify" ? "spotify-client" : "tidal-client",
                QueryValue(uri, "client_id"));
            callback = Task.Run(async () =>
            {
                using var callbackHttp = new HttpClient();
                string redirect = service == "spotify" ? settings.SpotifyRedirectUri : settings.TidalRedirectUri;
                string query = "?state=" + (correctState ? state : "invalid");
                if (provideCode) query += "&code=demo-code";
                using var response = await callbackHttp.GetAsync(redirect + query);
                response.EnsureSuccessStatusCode();
            });
        });
        var login = manager.Login(service);
        if (correctState && provideCode)
        {
            await login.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(1, server.Calls);
            Assert.Contains("grant_type=authorization_code", server.LastForm);
            Assert.Contains("code_verifier", server.LastForm);
            Assert.Equal("issued-access", await manager.AccessToken(service));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                login.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.Equal(0, server.Calls);
        }
        if (callback is not null) await callback.WaitAsync(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task OAuth_response_without_access_token_is_rejected()
    {
        int port = FreeLoopbackPort();
        var settings = Settings(port);
        var server = new FakeTokenServer
        {
            Body = JsonSerializer.Serialize(new OAuthTokenResponse { AccessToken = "" },
                AppJsonContext.Default.OAuthTokenResponse)
        };
        using var http = new HttpClient(server);
        var manager = new OAuthManager(TemporaryPath(), settings, http, url =>
        {
            var state = QueryValue(new Uri(url), "state");
            _ = Task.Run(async () =>
            {
                using var callbackHttp = new HttpClient();
                await callbackHttp.GetAsync(settings.SpotifyRedirectUri + "?state=" + state + "&code=c");
            });
        });
        await Assert.ThrowsAsync<JsonException>(() =>
            manager.Login("spotify").WaitAsync(TimeSpan.FromSeconds(12)));
    }
}
