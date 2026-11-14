using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SpotifyToTidal;

public abstract class HttpTrackLibrary(HttpClient http, Func<Task<string>> bearerToken,
    Uri baseUrl, string acceptType) : ITrackLibrary
{
    protected readonly HttpClient Http = http;
    protected readonly Uri BaseUrl = baseUrl;
    public abstract string Name { get; }

    // No object payloads or reflection-based serialization: the concrete wire type
    // must be provided by the compile-time generated JsonSerializerContext.
    protected async Task<TResponse> Request<TResponse>(
        HttpMethod method, string path, JsonTypeInfo<TResponse> responseType,
        string? serializedPayload = null, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await bearerToken());
            req.Headers.Accept.ParseAdd(acceptType);
            if (serializedPayload is not null)
                req.Content = new StringContent(serializedPayload, Encoding.UTF8, acceptType);

            using var response = await Http.SendAsync(req, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt == 4)
                    throw new HttpRequestException($"{Name} returned {(int)response.StatusCode} after retries.");
                var retry = response.Headers.RetryAfter?.Delta ??
                    TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(retry > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retry, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{Name} returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}");
            return JsonSerializer.Deserialize(string.IsNullOrWhiteSpace(body) ? "{}" : body, responseType)
                ?? throw new JsonException($"{Name} returned null JSON.");
        }
        throw new InvalidOperationException("Unreachable.");
    }

    protected static string Escape(string s) => Uri.EscapeDataString(s);
    public abstract Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default);
    public abstract Task AddFavorite(string trackId, CancellationToken ct = default);
}

public sealed class SpotifyLibrary(HttpClient http, Func<Task<string>> token, Uri baseUrl)
    : HttpTrackLibrary(http, token, baseUrl, "application/json")
{
    public override string Name => "Spotify";

    static Track? Parse(SpotifyTrackDto? dto)
    {
        if (string.IsNullOrWhiteSpace(dto?.Id) || dto.IsLocal) return null;
        return new Track(dto.Id, dto.Name ?? "",
            dto.Artists.FirstOrDefault()?.Name ?? "", dto.ExternalIds?.Isrc);
    }

    public override async Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default)
    {
        List<Track> tracks = [];
        string? next = "/v1/me/tracks?limit=50";
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            var page = await Request(HttpMethod.Get, next,
                AppJsonContext.Default.SpotifySavedTracksPage, ct: ct);
            foreach (var item in page.Items)
                if (Parse(item.Track) is { } song) tracks.Add(song);
            next = page.Next;
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
            var page = await Request(HttpMethod.Get, path,
                AppJsonContext.Default.SpotifySearchResponse, ct: ct);
            var items = page.Tracks?.Items ?? [];
            foreach (var item in items)
                if (Parse(item) is { } song &&
                    string.Equals(song.Isrc, isrc, StringComparison.OrdinalIgnoreCase))
                    tracks.Add(song);
            if (items.Length < 10) break;
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task AddFavorite(string trackId, CancellationToken ct = default)
    {
        _ = await Request(HttpMethod.Put,
            "/v1/me/library?uris=" + Escape("spotify:track:" + trackId),
            AppJsonContext.Default.EmptyApiResponse, ct: ct);
    }
}

public sealed class TidalLibrary(HttpClient http, Func<Task<string>> token, Uri baseUrl,
    string countryCode = "US")
    : HttpTrackLibrary(http, token, baseUrl, "application/vnd.tidal.v1+json")
{
    public override string Name => "TIDAL";
    readonly string country = countryCode;

    static Track? Parse(TidalTrackDto? item, IReadOnlyList<TidalArtistDto> included)
    {
        if (string.IsNullOrWhiteSpace(item?.Id)) return null;
        var ids = (item.Relationships?.Artists?.Data ?? [])
            .Select(a => a.Id).Where(id => id is not null).ToHashSet();
        var artist = included.FirstOrDefault(a => a.Type == "artists" && ids.Contains(a.Id))
            ?.Attributes?.Name ?? item.Attributes?.Artist ?? "";
        return new Track(item.Id, item.Attributes?.Title ?? "", artist, item.Attributes?.Isrc);
    }

    public override async Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default)
    {
        List<Track> tracks = [];
        string? next = "/v2/userCollectionTracks/me/relationships/items?countryCode=" + Escape(country);
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            var page = await Request(HttpMethod.Get, next,
                AppJsonContext.Default.TidalCollectionPage, ct: ct);
            foreach (var entry in page.Data)
            {
                if (entry.Id is null) continue;
                var detail = await Request(HttpMethod.Get, "/v2/tracks/" + Escape(entry.Id) +
                    "?countryCode=" + Escape(country) + "&include=artists",
                    AppJsonContext.Default.TidalTrackDetail, ct: ct);
                if (Parse(detail.Data, detail.Included) is { } song)
                    tracks.Add(song);
            }
            next = page.Links?.Next;
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
            var page = await Request(HttpMethod.Get, next,
                AppJsonContext.Default.TidalTrackPage, ct: ct);
            foreach (var entry in page.Data)
            {
                var track = Parse(entry, page.Included);
                if (track is not null &&
                    string.Equals(track.Isrc, isrc, StringComparison.OrdinalIgnoreCase))
                    tracks.Add(track);
            }
            next = page.Links?.Next;
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    public override async Task AddFavorite(string trackId, CancellationToken ct = default)
    {
        var payload = new TidalCollectionWriteRequest
        {
            Data = [new TidalResourceDto { Id = trackId, Type = "tracks" }]
        };
        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.TidalCollectionWriteRequest);
        _ = await Request(HttpMethod.Post, "/v2/userCollectionTracks/me/relationships/items",
            AppJsonContext.Default.EmptyApiResponse, json, ct);
    }
}
