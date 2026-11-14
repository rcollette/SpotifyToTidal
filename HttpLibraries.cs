using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpotifyToTidal;

public abstract class HttpTrackLibrary(HttpClient http, Func<Task<string>> bearerToken, Uri baseUrl,
    string acceptType) : ITrackLibrary
{
    protected readonly HttpClient Http = http;
    protected readonly Uri BaseUrl = baseUrl;
    public abstract string Name { get; }

    protected async Task<JsonDocument> Request(HttpMethod method, string path, object? payload = null,
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await bearerToken());
            req.Headers.Accept.ParseAdd(acceptType);
            if (payload is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, acceptType);

            using var response = await Http.SendAsync(req, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt == 4) throw new HttpRequestException($"{Name} returned {(int)response.StatusCode} after retries.");
                var retry = response.Headers.RetryAfter?.Delta ??
                    TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(retry > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retry, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{Name} returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        throw new InvalidOperationException("Unreachable.");
    }
    protected static JsonElement Get(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value) ? value : default;
    protected static string? Str(JsonElement obj, string property) =>
        Get(obj, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    protected static IEnumerable<JsonElement> Items(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Array ? obj.EnumerateArray() : [];
    protected static string Escape(string s) => Uri.EscapeDataString(s);
    public abstract Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default);
    public abstract Task AddFavorite(string trackId, CancellationToken ct = default);
}

public sealed class SpotifyLibrary(HttpClient http, Func<Task<string>> token, Uri baseUrl)
    : HttpTrackLibrary(http, token, baseUrl, "application/json")
{
    public override string Name => "Spotify";
    static Track? Parse(JsonElement track)
    {
        var id = Str(track, "id");
        if (string.IsNullOrWhiteSpace(id) || Get(track, "is_local").ValueKind == JsonValueKind.True) return null;
        var artist = Items(Get(track, "artists")).Select(a => Str(a, "name")).FirstOrDefault() ?? "";
        return new Track(id, Str(track, "name") ?? "", artist, Str(Get(track, "external_ids"), "isrc"));
    }

    public override async Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default)
    {
        List<Track> tracks = [];
        string? next = "/v1/me/tracks?limit=50";
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            using var doc = await Request(HttpMethod.Get, next, ct: ct);
            foreach (var entry in Items(Get(doc.RootElement, "items")))
                if (Parse(Get(entry, "track")) is { } song) tracks.Add(song);
            next = Str(doc.RootElement, "next");
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default)
    {
        List<Track> tracks = [];
        for (int offset = 0; offset < 100; offset += 10)
        {
            string path = "/v1/search?type=track&limit=10&offset=" + offset +
                "&q=" + Escape("isrc:" + isrc);
            using var doc = await Request(HttpMethod.Get, path, ct: ct);
            var items = Items(Get(Get(doc.RootElement, "tracks"), "items")).ToArray();
            foreach (var entry in items)
                if (Parse(entry) is { } song &&
                    string.Equals(song.Isrc, isrc, StringComparison.OrdinalIgnoreCase)) tracks.Add(song);
            if (items.Length < 10) break;
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task AddFavorite(string trackId, CancellationToken ct = default)
    {
        using var _ = await Request(HttpMethod.Put,
            "/v1/me/library?uris=" + Escape("spotify:track:" + trackId), ct: ct);
    }
}

public sealed class TidalLibrary(HttpClient http, Func<Task<string>> token, Uri baseUrl,
    string countryCode = "US") : HttpTrackLibrary(http, token, baseUrl, "application/vnd.tidal.v1+json")
{
    public override string Name => "TIDAL";
    readonly string country = countryCode;

    static Track? Parse(JsonElement item, JsonElement included)
    {
        string? id = Str(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var attrs = Get(item, "attributes");
        var artistIds = Items(Get(Get(Get(item, "relationships"), "artists"), "data"))
            .Select(a => Str(a, "id")).Where(s => s is not null).ToHashSet();
        string artist = Items(included)
            .Where(a => Str(a, "type") == "artists" && artistIds.Contains(Str(a, "id")))
            .Select(a => Str(Get(a, "attributes"), "name")).FirstOrDefault() ??
            Str(attrs, "artist") ?? "";
        return new Track(id, Str(attrs, "title") ?? "", artist, Str(attrs, "isrc"));
    }

    public override async Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default)
    {
        List<Track> tracks = [];
        string? next = "/v2/userCollectionTracks/me/relationships/items?countryCode=" + Escape(country);
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            using var doc = await Request(HttpMethod.Get, next, ct: ct);
            foreach (var entry in Items(Get(doc.RootElement, "data")))
            {
                var id = Str(entry, "id");
                if (id is null) continue;
                using var detail = await Request(HttpMethod.Get, "/v2/tracks/" + Escape(id) +
                    "?countryCode=" + Escape(country) + "&include=artists", ct: ct);
                if (Parse(Get(detail.RootElement, "data"), Get(detail.RootElement, "included")) is { } song)
                    tracks.Add(song);
            }
            next = Str(Get(doc.RootElement, "links"), "next");
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default)
    {
        List<Track> tracks = [];
        string? next = "/v2/tracks?filter%5Bisrc%5D=" + Escape(isrc) +
            "&countryCode=" + Escape(country) + "&include=artists";
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            using var doc = await Request(HttpMethod.Get, next, ct: ct);
            foreach (var entry in Items(Get(doc.RootElement, "data")))
            {
                var track = Parse(entry, Get(doc.RootElement, "included"));
                if (track is not null &&
                    string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase)) tracks.Add(track);
            }
            next = Str(Get(doc.RootElement, "links"), "next");
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task AddFavorite(string trackId, CancellationToken ct = default)
    {
        var payload = new { data = new[] { new { id = trackId, type = "tracks" } } };
        using var _ = await Request(HttpMethod.Post,
            "/v2/userCollectionTracks/me/relationships/items", payload, ct);
    }
}
