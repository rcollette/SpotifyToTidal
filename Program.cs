using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyToTidal;

public sealed record AppSettings(string SpotifyClientId, string TidalClientId,
    string SpotifyRedirectUri, string TidalRedirectUri, string CountryCode,
    string TidalScopes = "collection.read collection.write",
    int? SpotifyRequestsPer30Seconds = null, int? TidalRequestsPerMinute = null);

public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

public sealed class OAuthManager(string dataDirectory, AppSettings settings, HttpClient http,
    Action<string>? browserOpener = null)
{
    // Prevent concurrent requests from refreshing the same OAuth token
    // repeatedly or overwriting newer refresh tokens.
    readonly SemaphoreSlim spotifyTokenGate = new(1, 1);
    readonly SemaphoreSlim tidalTokenGate = new(1, 1);

    string TokenPath(string service) => Path.Combine(dataDirectory, service + "-token.json");
    string ClientId(string service) => service == "spotify" ? settings.SpotifyClientId : settings.TidalClientId;
    string Redirect(string service) => service == "spotify" ? settings.SpotifyRedirectUri : settings.TidalRedirectUri;
    string AuthorizationUrl(string service) => service == "spotify"
        ? "https://accounts.spotify.com/authorize" : "https://login.tidal.com/authorize";
    string TokenUrl(string service) => service == "spotify"
        ? "https://accounts.spotify.com/api/token" : "https://auth.tidal.com/v1/oauth2/token";
    string Scopes(string service) => service == "spotify"
        ? "user-library-read user-library-modify" : settings.TidalScopes;
    static string Encode(string s) => Uri.EscapeDataString(s);
    static string RandomValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(40))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task Login(string service)
    {
        if (string.IsNullOrWhiteSpace(ClientId(service)) || ClientId(service).StartsWith("REPLACE_"))
            throw new InvalidOperationException("Fill in the " + service + " client ID in config.json.");
        var callback = new Uri(Redirect(service));
        if (callback.Scheme != "http" || callback.Host != "127.0.0.1" ||
            !callback.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("Use an HTTP 127.0.0.1 callback with a trailing slash.");
        string verifier = RandomValue(), state = RandomValue();
        string challenge = Convert.ToBase64String(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var listener = new HttpListener();
        listener.Prefixes.Add(callback.ToString());
        listener.Start();
        string url = AuthorizationUrl(service) + "?response_type=code&client_id=" +
            Encode(ClientId(service)) + "&redirect_uri=" + Encode(Redirect(service)) +
            "&scope=" + Encode(Scopes(service)) + "&state=" + state +
            "&code_challenge_method=S256&code_challenge=" + challenge;
        Console.WriteLine("Open in your browser:\n" + url);
        try
        {
            if (browserOpener is null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else browserOpener(url);
        }
        catch { /* URL printed for headless terminals. */ }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = await listener.GetContextAsync().WaitAsync(cts.Token);
        var code = context.Request.QueryString["code"];
        bool valid = context.Request.QueryString["state"] == state &&
            !string.IsNullOrWhiteSpace(code);
        byte[] message = Encoding.UTF8.GetBytes(valid ? "Authorized; return to terminal." :
            "Authorization failed; return to terminal.");
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = message.Length;
        await context.Response.OutputStream.WriteAsync(message);
        context.Response.Close();
        if (!valid) throw new InvalidOperationException("OAuth callback was rejected or denied.");
        await Exchange(service, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ClientId(service),
            ["code"] = code!, ["redirect_uri"] = Redirect(service), ["code_verifier"] = verifier
        }, null);
    }

    async Task<OAuthToken> Exchange(string service, Dictionary<string, string> fields, string? refresh)
    {
        using var response = await http.PostAsync(TokenUrl(service), new FormUrlEncodedContent(fields));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{service} token request failed ({(int)response.StatusCode}): {body}");
        var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.OAuthTokenResponse)
            ?? throw new JsonException("OAuth token response was null.");
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new JsonException("OAuth token response omitted the access token.");
        var token = new OAuthToken(result.AccessToken, result.RefreshToken ?? refresh,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, result.ExpiresIn - 90)));
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(TokenPath(service), JsonSerializer.Serialize(token, AppJsonContext.Default.OAuthToken));
        return token;
    }

    public async Task<string> AccessToken(string service)
    {
        var gate = service == "spotify" ? spotifyTokenGate : tidalTokenGate;
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(TokenPath(service)))
                throw new InvalidOperationException("Run 'login " + service + "' first.");
            var token = JsonSerializer.Deserialize<OAuthToken>(
                await File.ReadAllTextAsync(TokenPath(service)), AppJsonContext.Default.OAuthToken)!;
            if (token.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (token.RefreshToken is null)
                    throw new InvalidOperationException(service + " token expired; login again.");
                token = await Exchange(service, new()
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = ClientId(service),
                    ["refresh_token"] = token.RefreshToken
                }, token.RefreshToken);
            }
            return token.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

}

public static class Program
{
    static void Help() => Console.WriteLine(
        """
        SpotifyToTidal — Spotify Liked Songs / TIDAL Favorite Tracks

        Usage:
          SpotifyToTidal login spotify|tidal
          SpotifyToTidal sync [options]
          SpotifyToTidal report
          SpotifyToTidal help

        Sync options:
          --direction both|spotify-to-tidal|tidal-to-spotify
                                      Direction (default: both)
          --dry-run                   Preview additions and cleanup; make no changes
          --max-concurrency N         Concurrent operations per service (1-32; default: 5)
                                      Rate limits are controlled in config.json;
                                      provider-wide HTTP 429 cooldowns are automatic
          --prune-unavailable-tidal   DELETE TIDAL favorites with unavailable metadata
                                      (destructive; disabled by default)

        Cleanup examples:
          SpotifyToTidal sync --dry-run --prune-unavailable-tidal
          SpotifyToTidal sync --prune-unavailable-tidal

        Help: SpotifyToTidal --help  or  SpotifyToTidal sync --help
        """);

    public static Task<int> Main(string[] args) =>
        Run(args, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spotify-to-tidal"));

    // An injectable HTTP transport and token directory make the real CLI testable
    // without external services, global HOME changes, or OAuth credentials.
    public static async Task<int> Run(string[] args, string dataDir, HttpClient? providedHttp = null)
    {
        try
        {
            if (args.Length == 0 ||
                args is ["help" or "--help" or "-h"] or
                    ["sync", "--help" or "-h"])
            {
                Help(); return 0;
            }
            Directory.CreateDirectory(dataDir);
            if (args[0] == "report")
            {
                Console.WriteLine(Path.Combine(dataDir, "report.csv")); return 0;
            }
            string configPath = Path.Combine(dataDir, "config.json");
            if (!File.Exists(configPath))
                throw new InvalidOperationException("Copy appsettings.example.json to " + configPath + " and configure client IDs.");
            var settings = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configPath), AppJsonContext.Default.AppSettings)
                ?? throw new JsonException("Config JSON was null.");
            using var ownedHttp = providedHttp is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(40) } : null;
            var http = providedHttp ?? ownedHttp!;
            var auth = new OAuthManager(dataDir, settings, http);
            if (args[0] == "login" && args.Length == 2 && args[1] is "spotify" or "tidal")
            {
                await auth.Login(args[1]); return 0;
            }
            if (args[0] != "sync") { Help(); return 2; }

            bool dryRun = false, pruneUnavailableTidal = false;
            int maxConcurrency = 5;
            SyncDirection direction = SyncDirection.Both;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--dry-run") dryRun = true;
                else if (args[i] == "--direction" && i + 1 < args.Length)
                {
                    direction = args[++i] switch
                    {
                        "both" => SyncDirection.Both,
                        "spotify-to-tidal" => SyncDirection.SpotifyToTidal,
                        "tidal-to-spotify" => SyncDirection.TidalToSpotify,
                        _ => throw new ArgumentException("Invalid direction")
                    };
                }
                else if (args[i] == "--prune-unavailable-tidal") pruneUnavailableTidal = true;
                else if (args[i] == "--max-concurrency" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out maxConcurrency) ||
                        maxConcurrency is < 1 or > 32)
                        throw new ArgumentException("--max-concurrency must be between 1 and 32.");
                }
                else throw new ArgumentException("Unknown sync argument " + args[i]);
            }

            if (settings.SpotifyRequestsPer30Seconds is <= 0 ||
                settings.TidalRequestsPerMinute is <= 0)
                throw new ArgumentException("Configured API request budgets must be positive integers.");
            var spotifyRate = new ApiRateLimiter("Spotify",
                settings.SpotifyRequestsPer30Seconds, TimeSpan.FromSeconds(30));
            var tidalRate = new ApiRateLimiter("TIDAL",
                settings.TidalRequestsPerMinute, TimeSpan.FromMinutes(1));
            var coordinator = new SyncConsoleProgress(Console.Out);

            Console.WriteLine("Synchronizing with up to " + maxConcurrency +
                " concurrent operations per service.");
            Console.WriteLine("Local request budgets: Spotify " +
                (settings.SpotifyRequestsPer30Seconds is { } spotifyBudget
                    ? spotifyBudget + "/30s" : "not configured (vendor numeric cap unpublished)") +
                "; TIDAL " +
                (settings.TidalRequestsPerMinute is { } tidalBudget
                    ? tidalBudget + "/60s" : "not configured (vendor numeric cap unpublished)") + ".");
            var spotify = new SpotifyLibrary(http, () => auth.AccessToken("spotify"),
                new Uri("https://api.spotify.com/"), rateLimiter: spotifyRate);
            var tidal = new TidalLibrary(http, () => auth.AccessToken("tidal"),
                new Uri("https://openapi.tidal.com/"), settings.CountryCode,
                maxConcurrency: maxConcurrency, deleteUnavailable: pruneUnavailableTidal,
                dryRun: dryRun, progress: coordinator.Report, rateLimiter: tidalRate);
            var results = await SyncEngine.Run(spotify, tidal, direction, dryRun,
                maxConcurrency: maxConcurrency, progress: coordinator.Report);
            string path = Path.Combine(dataDir, "report.csv");
            await File.WriteAllTextAsync(path, CsvReport.Serialize(results));
            foreach (var group in results.GroupBy(r => (r.Direction, r.Status)))
                Console.WriteLine($"{group.Key.Direction} {group.Key.Status}: {group.Count()}");
            Console.WriteLine("Report: " + path);
            return results.Any(r => r.Status == "error") ? 1 : 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return 1; }
    }
}
