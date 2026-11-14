using System.Net;
using System.Text;
using System.Text.Json;
using SpotifyToTidal;
using Xunit;

// Black-box acceptance scenarios: real adapters + JSON + HTTP verbs + the sync engine,
// with an in-memory HTTP server replacement. No personal accounts, credentials, or Docker.
sealed class FakeMusicApi : HttpMessageHandler
{
    public readonly HashSet<string> SpotifyFavorites = ["spot-A", "spot-B"];
    public readonly HashSet<string> TidalFavorites = ["tid-C"];
    public int SpotifyWrites, TidalWrites, SpotifyReads, TidalReads;
    public bool AmbiguousTidalSearch;
    readonly Dictionary<string, Track> spotify = new()
    {
        ["spot-A"] = new("spot-A", "North Star", "Artist One", "USAAA0000001"),
        ["spot-B"] = new("spot-B", "Unpublished Demo", "Artist Two", null),
        ["spot-C"] = new("spot-C", "Ocean Blue", "Artist Three", "USAAA0000003")
    };
    readonly Dictionary<string, Track> tidal = new()
    {
        ["tid-A"] = new("tid-A", "North Star", "Artist One", "USAAA0000001"),
        ["tid-A-copy"] = new("tid-A-copy", "North Star", "Artist One", "USAAA0000001"),
        ["tid-C"] = new("tid-C", "Ocean Blue", "Artist Three", "USAAA0000003")
    };

    static object SpotifyTrack(Track t) => new
    {
        id = t.Id, name = t.Title, artists = new[] { new { name = t.Artist } },
        external_ids = new { isrc = t.Isrc }, is_local = false
    };
    static object TidalTrack(Track t) => new
    {
        id = t.Id, type = "tracks", attributes = new { title = t.Title, isrc = t.Isrc },
        relationships = new { artists = new { data = new[] {
            new { id = "artist-" + t.Id, type = "artists" } } } }
    };
    static object TidalArtist(Track t) => new
    {
        id = "artist-" + t.Id, type = "artists", attributes = new { name = t.Artist }
    };
    static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new Exception("No URI");
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        if (uri.Host == "spotify.test")
        {
            if (path == "/v1/me/tracks" && request.Method == HttpMethod.Get)
            {
                SpotifyReads++;
                return Json(new { items = SpotifyFavorites.Select(id => new {
                    track = SpotifyTrack(spotify[id])
                }).ToArray(), next = (string?)null });
            }
            if (path == "/v1/search" && request.Method == HttpMethod.Get)
            {
                string isrc = spotify.Values.Select(t => t.Isrc).FirstOrDefault(isrc =>
                    isrc is not null && query.Contains(isrc, StringComparison.OrdinalIgnoreCase)) ?? "";
                return Json(new { tracks = new { items = spotify.Values.Where(t => t.Isrc == isrc && isrc != "")
                    .Select(SpotifyTrack).ToArray() } });
            }
            if (path == "/v1/me/library" && request.Method == HttpMethod.Put)
            {
                string target = spotify.Keys.FirstOrDefault(id => query.Contains("spotify:track:" + id)) ?? "";
                if (target == "") throw new Exception("Unexpected Spotify PUT " + query);
                SpotifyWrites++; SpotifyFavorites.Add(target);
                return Json(new { });
            }
        }

        if (uri.Host == "tidal.test")
        {
            if (!request.Headers.Accept.Any(h => h.MediaType == "application/vnd.tidal.v1+json"))
                throw new Exception("TIDAL media-type header absent");
            if (path == "/v2/userCollectionTracks/me/relationships/items" && request.Method == HttpMethod.Get)
            {
                TidalReads++;
                return Json(new { data = TidalFavorites.Select(id => new { id, type = "tracks" }).ToArray(),
                    links = new { next = (string?)null } });
            }
            if (path.StartsWith("/v2/tracks/") && request.Method == HttpMethod.Get)
            {
                var id = Uri.UnescapeDataString(path["/v2/tracks/".Length..]);
                var track = tidal[id];
                return Json(new { data = TidalTrack(track), included = new[] { TidalArtist(track) } });
            }
            if (path == "/v2/tracks" && request.Method == HttpMethod.Get)
            {
                string isrc = tidal.Values.Select(t => t.Isrc).FirstOrDefault(isrc =>
                    isrc is not null && query.Contains(isrc, StringComparison.OrdinalIgnoreCase)) ?? "";
                var matches = tidal.Values.Where(t => t.Isrc == isrc && isrc != "" &&
                    (AmbiguousTidalSearch || t.Id != "tid-A-copy")).ToArray();
                return Json(new { data = matches.Select(TidalTrack).ToArray(),
                    included = matches.Select(TidalArtist).ToArray(),
                    links = new { next = (string?)null } });
            }
            if (path == "/v2/userCollectionTracks/me/relationships/items" && request.Method == HttpMethod.Post)
            {
                using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                string id = content.RootElement.GetProperty("data")[0].GetProperty("id").GetString()!;
                if (!tidal.ContainsKey(id)) throw new Exception("Unknown TIDAL favorite ID");
                TidalWrites++; TidalFavorites.Add(id);
                return Json(new { data = new { } });
            }
        }

        throw new Exception("Unexpected request: " + request.Method + " " + uri);
    }
}

public class AcceptanceTests
{
    static void Assert(bool condition, string reason = "Acceptance assertion failed")
    {
        if (!condition) throw new Exception(reason);
    }
    static (SpotifyLibrary Spotify, TidalLibrary Tidal, HttpClient Client) Setup(FakeMusicApi fake)
    {
        var http = new HttpClient(fake);
        Func<Task<string>> token = () => Task.FromResult("test-bearer");
        return (new SpotifyLibrary(http, token, new Uri("https://spotify.test/")),
            new TidalLibrary(http, token, new Uri("https://tidal.test/")), http);
    }

    [Fact]
    public async Task Bidirectional_http_acceptance_scenarios()
    {
        async Task Check(string name, Func<Task> test) => await test();
        await Check("bidirectional dry-run previews both additions, never writes", async () =>
        {
            var fake = new FakeMusicApi();
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, dryRun: true);
                Assert(rows.Count(r => r.Status == "would-add") == 2, "Expected two preview additions");
                Assert(rows.Count(r => r.Status == "unmatched") == 1, "Expected missing-ISRC review");
                Assert(fake.SpotifyWrites == 0 && fake.TidalWrites == 0);
                Assert(fake.SpotifyReads > 0 && fake.TidalReads > 0);
                Assert(CsvReport.Serialize(rows).Contains("spotify-to-tidal"));
                Assert(CsvReport.Serialize(rows).Contains("tidal-to-spotify"));
            }
        });

        await Check("live sync adds both ways, is idempotent, and never deletes", async () =>
        {
            var fake = new FakeMusicApi();
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var first = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, false);
                Assert(first.Count(r => r.Status == "added") == 2);
                Assert(fake.TidalFavorites.Contains("tid-A") && fake.SpotifyFavorites.Contains("spot-C"));
                Assert(fake.SpotifyWrites == 1 && fake.TidalWrites == 1);
                var second = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, false);
                Assert(second.All(r => r.Status != "added"));
                Assert(fake.SpotifyWrites == 1 && fake.TidalWrites == 1);
                Assert(fake.SpotifyFavorites.Contains("spot-B"), "Existing favorite must never be removed");
            }
        });

        await Check("one-way Spotify to TIDAL does not write Spotify", async () =>
        {
            var fake = new FakeMusicApi();
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
                Assert(rows.All(r => r.Direction == "spotify-to-tidal"));
                Assert(fake.TidalWrites == 1 && fake.SpotifyWrites == 0);
            }
        });

        await Check("one-way TIDAL to Spotify does not write TIDAL", async () =>
        {
            var fake = new FakeMusicApi();
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.TidalToSpotify, false);
                Assert(rows.All(r => r.Direction == "tidal-to-spotify"));
                Assert(fake.SpotifyWrites == 1 && fake.TidalWrites == 0);
            }
        });

        await Check("ambiguous destination candidates are never automatically liked", async () =>
        {
            var fake = new FakeMusicApi { AmbiguousTidalSearch = true };
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
                Assert(rows.Any(r => r.Status == "unmatched" && r.Note.Contains("Multiple")));
                Assert(fake.TidalWrites == 0);
            }
        });

        await Check("unknown-ISRC source does not cause an unsafe write", async () =>
        {
            var fake = new FakeMusicApi();
            fake.SpotifyFavorites.Remove("spot-A");
            var (spotify, tidal, http) = Setup(fake);
            using (http)
            {
                var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
                Assert(rows.Count == 1 && rows[0].Status == "unmatched");
                Assert(fake.SpotifyWrites == 0 && fake.TidalWrites == 0);
            }
        });

    }
}
