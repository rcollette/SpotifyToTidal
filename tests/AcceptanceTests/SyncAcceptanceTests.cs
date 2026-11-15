using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SpotifyToTidal;
using Xunit;

// HTTP-level acceptance tests use generated, strongly typed fixture contracts.
// No credentials, reflection-based JSON, live API calls, or Docker are required.
sealed class FakeMusicApi : HttpMessageHandler
{
    public readonly HashSet<string> SpotifyFavorites = ["spot-A", "spot-B"];
    public readonly HashSet<string> TidalFavorites = ["tid-C"];
    public int SpotifyWrites, TidalWrites, SpotifyReads, TidalReads;
    public bool AmbiguousTidalSearch;
    public bool RejectTidalWrites;
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

    static SpotifyTrackDto SpotifyTrack(Track t) => new()
    {
        Id = t.Id, Name = t.Title, Artists = [new SpotifyArtistDto { Name = t.Artist }],
        ExternalIds = new SpotifyExternalIdsDto { Isrc = t.Isrc }
    };
    static TidalTrackDto TidalTrack(Track t) => new()
    {
        Id = t.Id, Type = "tracks",
        Attributes = new TidalTrackAttributesDto { Title = t.Title, Isrc = t.Isrc },
        Relationships = new TidalRelationshipsDto
        {
            Artists = new TidalArtistRelationshipDto
            {
                Data = [new TidalResourceDto { Id = "artist-" + t.Id, Type = "artists" }]
            }
        }
    };
    static TidalArtistDto TidalArtist(Track t) => new()
    {
        Id = "artist-" + t.Id, Type = "artists",
        Attributes = new TidalArtistAttributesDto { Name = t.Artist }
    };
    static HttpResponseMessage Json<T>(T payload, JsonTypeInfo<T> typeInfo) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, typeInfo),
            Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new Exception("No URI");
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        if (uri.Host is "spotify.test" or "api.spotify.com")
        {
            if (path == "/v1/me/tracks" && request.Method == HttpMethod.Get)
            {
                SpotifyReads++;
                return Json(new SpotifySavedTracksPage
                {
                    Items = SpotifyFavorites.Select(id =>
                        new SpotifySavedItem { Track = SpotifyTrack(spotify[id]) }).ToArray()
                }, AppJsonContext.Default.SpotifySavedTracksPage);
            }
            if (path == "/v1/search" && request.Method == HttpMethod.Get)
            {
                string isrc = spotify.Values.Select(t => t.Isrc).FirstOrDefault(isrc =>
                    isrc is not null && query.Contains(isrc, StringComparison.OrdinalIgnoreCase)) ?? "";
                return Json(new SpotifySearchResponse
                {
                    Tracks = new SpotifyTrackPage
                    {
                        Items = spotify.Values.Where(t => t.Isrc == isrc && isrc != "")
                            .Select(SpotifyTrack).ToArray()
                    }
                }, AppJsonContext.Default.SpotifySearchResponse);
            }
            if (path == "/v1/me/library" && request.Method == HttpMethod.Put)
            {
                string target = spotify.Keys.FirstOrDefault(id =>
                    query.Contains("spotify:track:" + id)) ?? "";
                if (target == "") throw new Exception("Unexpected Spotify PUT " + query);
                SpotifyWrites++; SpotifyFavorites.Add(target);
                return Json(new EmptyApiResponse(), AppJsonContext.Default.EmptyApiResponse);
            }
        }
        if (uri.Host is "tidal.test" or "openapi.tidal.com")
        {
            if (!request.Headers.Accept.Any(h => h.MediaType == "application/vnd.tidal.v1+json"))
                throw new Exception("TIDAL media-type header absent");
            if (path == "/v2/userCollectionTracks/me/relationships/items" && request.Method == HttpMethod.Get)
            {
                TidalReads++;
                return Json(new TidalCollectionPage
                {
                    Data = TidalFavorites.Select(id =>
                        new TidalResourceDto { Id = id, Type = "tracks" }).ToArray(),
                    Links = new TidalLinksDto()
                }, AppJsonContext.Default.TidalCollectionPage);
            }
            if (path.StartsWith("/v2/tracks/") && request.Method == HttpMethod.Get)
            {
                var id = Uri.UnescapeDataString(path["/v2/tracks/".Length..]);
                var track = tidal[id];
                return Json(new TidalTrackDetail
                {
                    Data = TidalTrack(track), Included = [TidalArtist(track)]
                }, AppJsonContext.Default.TidalTrackDetail);
            }
            if (path == "/v2/tracks" && request.Method == HttpMethod.Get)
            {
                string isrc = tidal.Values.Select(t => t.Isrc).FirstOrDefault(isrc =>
                    isrc is not null && query.Contains(isrc, StringComparison.OrdinalIgnoreCase)) ?? "";
                var matches = tidal.Values.Where(t => t.Isrc == isrc && isrc != "" &&
                    (AmbiguousTidalSearch || t.Id != "tid-A-copy")).ToArray();
                return Json(new TidalTrackPage
                {
                    Data = matches.Select(TidalTrack).ToArray(),
                    Included = matches.Select(TidalArtist).ToArray(),
                    Links = new TidalLinksDto()
                }, AppJsonContext.Default.TidalTrackPage);
            }
            if (path == "/v2/userCollectionTracks/me/relationships/items" && request.Method == HttpMethod.Post)
            {
                if (RejectTidalWrites) return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("write denied")
                };
                var body = await request.Content!.ReadAsStringAsync(ct);
                var content = JsonSerializer.Deserialize(body, AppJsonContext.Default.TidalCollectionWriteRequest)
                    ?? throw new Exception("No collection request");
                string id = content.Data[0].Id!;
                if (!tidal.ContainsKey(id)) throw new Exception("Unknown TIDAL favorite ID");
                TidalWrites++; TidalFavorites.Add(id);
                return Json(new EmptyApiResponse(), AppJsonContext.Default.EmptyApiResponse);
            }
        }
        throw new Exception("Unexpected request: " + request.Method + " " + uri);
    }
}

public class SyncAcceptanceTests
{
    private static (SpotifyLibrary Spotify, TidalLibrary Tidal, HttpClient Client) Setup(FakeMusicApi fake)
    {
        var http = new HttpClient(fake);
        Func<Task<string>> token = () => Task.FromResult("test-bearer");
        return (new SpotifyLibrary(http, token, new Uri("https://spotify.test/")),
            new TidalLibrary(http, token, new Uri("https://tidal.test/")), http);
    }

    [Fact]
    public async Task Dry_run_previews_both_additions_and_never_writes()
    {
        var fake = new FakeMusicApi();
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, dryRun: true);
            Assert.Equal(2, rows.Count(r => r.Status == "would-add"));
            Assert.Single(rows, r => r.Status == "unmatched");
            Assert.Equal(0, fake.SpotifyWrites);
            Assert.Equal(0, fake.TidalWrites);
            Assert.True(fake.SpotifyReads > 0 && fake.TidalReads > 0);
            Assert.Contains("spotify-to-tidal", CsvReport.Serialize(rows));
            Assert.Contains("tidal-to-spotify", CsvReport.Serialize(rows));
        }
    }

    [Fact]
    public async Task Live_sync_is_additive_idempotent_and_never_removes()
    {
        var fake = new FakeMusicApi();
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var first = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, false);
            Assert.Equal(2, first.Count(r => r.Status == "added"));
            Assert.Contains("tid-A", fake.TidalFavorites);
            Assert.Contains("spot-C", fake.SpotifyFavorites);
            Assert.Equal(1, fake.SpotifyWrites);
            Assert.Equal(1, fake.TidalWrites);
            var second = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, false);
            Assert.DoesNotContain(second, r => r.Status == "added");
            Assert.Equal(1, fake.SpotifyWrites);
            Assert.Equal(1, fake.TidalWrites);
            Assert.Contains("spot-B", fake.SpotifyFavorites);
        }
    }

    [Fact]
    public async Task Spotify_to_Tidal_does_not_write_Spotify()
    {
        var fake = new FakeMusicApi();
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
            Assert.All(rows, r => Assert.Equal("spotify-to-tidal", r.Direction));
            Assert.Equal(1, fake.TidalWrites);
            Assert.Equal(0, fake.SpotifyWrites);
        }
    }

    [Fact]
    public async Task Tidal_to_Spotify_does_not_write_Tidal()
    {
        var fake = new FakeMusicApi();
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.TidalToSpotify, false);
            Assert.All(rows, r => Assert.Equal("tidal-to-spotify", r.Direction));
            Assert.Equal(1, fake.SpotifyWrites);
            Assert.Equal(0, fake.TidalWrites);
        }
    }

    [Fact]
    public async Task Ambiguous_Tidal_candidates_are_never_added()
    {
        var fake = new FakeMusicApi { AmbiguousTidalSearch = true };
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
            Assert.Contains(rows, r => r.Status == "unmatched" && r.Note.Contains("Multiple"));
            Assert.Equal(0, fake.TidalWrites);
        }
    }

    [Fact]
    public async Task Missing_ISRC_never_causes_a_write()
    {
        var fake = new FakeMusicApi();
        fake.SpotifyFavorites.Remove("spot-A");
        var (spotify, tidal, http) = Setup(fake);
        using (http)
        {
            var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.SpotifyToTidal, false);
            Assert.Single(rows);
            Assert.Equal("unmatched", rows[0].Status);
            Assert.Equal(0, fake.SpotifyWrites);
            Assert.Equal(0, fake.TidalWrites);
        }
    }
}
