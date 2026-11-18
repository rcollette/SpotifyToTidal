using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SpotifyToTidal;

public abstract class HttpTrackLibrary(HttpClient http, Func<Task<string>> bearerToken,
    Uri baseUrl, string acceptType,
    Func<TimeSpan, CancellationToken, Task>? retryDelay = null) : ITrackLibrary
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
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await bearerToken());
            req.Headers.Accept.ParseAdd(acceptType);
            if (serializedPayload is not null)
            {
                req.Content = new StringContent(serializedPayload, Encoding.UTF8);
                // JSON:API disallows an implicit "charset=utf-8" media-type parameter.
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(acceptType);
            }

            using var response = await Http.SendAsync(req, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt == 4)
                    throw new HttpRequestException($"{Name} returned {(int)response.StatusCode} after retries.");
                var retry = response.Headers.RetryAfter?.Delta ??
                    TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await (retryDelay ?? Task.Delay)(retry > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retry, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{Name} returned {(int)response.StatusCode} for {req.Method} {req.RequestUri}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
            return JsonSerializer.Deserialize(string.IsNullOrWhiteSpace(body) ? "{}" : body, responseType)
                ?? throw new JsonException($"{Name} returned null JSON.");
        }
    }

    protected static string Escape(string s) => Uri.EscapeDataString(s);
    public abstract Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default);
    public abstract Task AddFavorite(string trackId, CancellationToken ct = default);
}

public sealed class SpotifyLibrary(HttpClient http, Func<Task<string>> token, Uri baseUrl,
    Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    : HttpTrackLibrary(http, token, baseUrl, "application/json", retryDelay)
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
    string countryCode = "US", Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
    int maxConcurrency = 5, bool deleteUnavailable = false, bool dryRun = false,
    Action<SyncProgress>? progress = null)
    : HttpTrackLibrary(http, token, baseUrl, "application/vnd.api+json", retryDelay)
{
    public override string Name => "TIDAL";
    readonly string country = countryCode;

    // TIDAL pagination links are sometimes relative to the /v2 API root
    // rather than to the hostname root. Follow them without losing /v2.
    static string? NextPage(string? href, string currentPath)
    {
        if (href is null) return null;
        if (href.StartsWith("?", StringComparison.Ordinal))
            return currentPath.Split('?')[0] + href;
        if (href.StartsWith("/v2/", StringComparison.Ordinal))
            return href;
        if (href.StartsWith("/", StringComparison.Ordinal))
            return "/v2" + href;
        // Uri.TryCreate can treat slash-prefixed paths as absolute file URIs,
        // so test URI schemes only after handling rooted API paths above.
        if (Uri.TryCreate(href, UriKind.Absolute, out _)) return href;
        return "/v2/" + href;
    }

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
        // Enumerate the complete collection before hydrating or deleting it:
        // mutating a cursor-paginated collection during enumeration could skip IDs.
        List<string> ids = [];
        string? next = "/v2/userCollectionTracks/me/relationships/items";
        HashSet<string> pages = [];
        while (next is not null && pages.Add(next))
        {
            TidalCollectionPage page;
            try
            {
                page = await Request(HttpMethod.Get, next,
                    AppJsonContext.Default.TidalCollectionPage, ct: ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HttpRequestException(
                    ex.Message + " Check that your TIDAL developer app permits collection.read, " +
                    "then run 'login tidal' to authorize the new scope. If the collection endpoint " +
                    "still returns 404, ask TIDAL developer support whether track-collection " +
                    "access is enabled for your application.", ex, ex.StatusCode);
            }
            foreach (var item in page.Data)
                if (item.Id is not null) ids.Add(item.Id);
            next = NextPage(page.Links?.Next, next);
        }

        var trackIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        var tracks = new Track?[trackIds.Length];
        var missing = new ConcurrentQueue<string>();
        int resolved = 0, processed = 0;

        // Every cleanup candidate is confirmed via the single-track endpoint.
        // This avoids deleting legitimate favorites on a partial batch response.
        void Complete(int index, TidalTrackDto? detail,
            IReadOnlyList<TidalArtistDto> artists)
        {
            if (detail?.Attributes is null)
            {
                missing.Enqueue(trackIds[index]);
                Console.Error.WriteLine("Warning: TIDAL favorite " + trackIds[index] +
                    " has no available track metadata in " + country + "; skipped.");
            }
            else
            {
                Interlocked.Increment(ref resolved);
                tracks[index] = Parse(detail, artists);
            }
            progress?.Invoke(new SyncProgress("TIDAL metadata",
                Interlocked.Increment(ref processed), trackIds.Length));
        }

        async Task FetchIndividually(int index, CancellationToken token)
        {
            var id = trackIds[index];
            try
            {
                var detail = await Request(HttpMethod.Get, "/v2/tracks/" + Escape(id) +
                    "?countryCode=" + Escape(country) + "&include=artists",
                    AppJsonContext.Default.TidalTrackDetail, ct: token);
                Complete(index, detail.Data, detail.Included);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Complete(index, null, []);
            }
        }

        // TIDAL documents GET /tracks filter[id] with up to 20 track IDs.
        // Batches run concurrently, preserving the original favorites order.
        const int batchSize = 20;
        var batches = Enumerable.Range(0, (trackIds.Length + batchSize - 1) / batchSize);
        await Parallel.ForEachAsync(batches,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (batch, token) =>
            {
                int start = batch * batchSize;
                int count = Math.Min(batchSize, trackIds.Length - start);
                if (count == 1)
                {
                    await FetchIndividually(start, token);
                    return;
                }

                string? pagePath = "/v2/tracks?filter%5Bid%5D=" +
                    Escape(string.Join(",", trackIds.Skip(start).Take(count))) +
                    "&countryCode=" + Escape(country) + "&include=artists";
                var pages = new HashSet<string>(StringComparer.Ordinal);
                var found = new Dictionary<string, TidalTrackDto>(StringComparer.Ordinal);
                var artists = new List<TidalArtistDto>();
                try
                {
                    while (pagePath is not null && pages.Add(pagePath))
                    {
                        var page = await Request(HttpMethod.Get, pagePath,
                            AppJsonContext.Default.TidalTrackPage, ct: token);
                        foreach (var item in page.Data)
                            if (item?.Id is not null) found[item.Id] = item;
                        artists.AddRange(page.Included);
                        pagePath = NextPage(page.Links?.Next, pagePath);
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    // An unavailable ID or a provider limitation must not
                    // break sync or result in unverified deletion candidates.
                    for (int offset = 0; offset < count; offset++)
                        await FetchIndividually(start + offset, token);
                    return;
                }

                for (int offset = 0; offset < count; offset++)
                {
                    int index = start + offset;
                    if (found.TryGetValue(trackIds[index], out var detail) &&
                        detail.Attributes is not null)
                        Complete(index, detail, artists);
                    else
                        await FetchIndividually(index, token);
                }
            });

        if (trackIds.Length == 0)
            progress?.Invoke(new SyncProgress("TIDAL metadata", 0, 0));

        // If an entire collection appears unavailable, stop: incorrect scopes or
        // a service-side problem must NEVER trigger a mass deletion.
        if (!missing.IsEmpty && resolved == 0)
            throw new HttpRequestException(
                "Every TIDAL favorite track has missing or unavailable metadata (" +
                missing.Count + " tracks). Check the TIDAL app's catalog access and " +
                "config.json countryCode; refusing to treat the collection as empty.");

        if (deleteUnavailable && !missing.IsEmpty)
        {
            var removals = missing.ToArray();
            if (dryRun)
            {
                Console.WriteLine("Dry run: would remove " + removals.Length +
                    " TIDAL favorites with unavailable metadata.");
                progress?.Invoke(new SyncProgress("TIDAL cleanup preview",
                    removals.Length, removals.Length));
            }
            else
            {
                int deleted = 0;
                await Parallel.ForEachAsync(removals,
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                    async (id, token) =>
                    {
                        await RemoveFavorite(id, token);
                        Console.WriteLine("Removed unavailable TIDAL favorite " + id);
                        progress?.Invoke(new SyncProgress("TIDAL cleanup",
                            Interlocked.Increment(ref deleted), removals.Length));
                    });
            }
        }
        return tracks.OfType<Track>().ToList();
    }

    public async Task RemoveFavorite(string trackId, CancellationToken ct = default)
    {
        var payload = new TidalCollectionWriteRequest
        {
            Data = [new TidalResourceDto { Id = trackId, Type = "tracks" }]
        };
        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.TidalCollectionWriteRequest);
        _ = await Request(HttpMethod.Delete, "/v2/userCollectionTracks/me/relationships/items",
            AppJsonContext.Default.EmptyApiResponse, json, ct);
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
            next = NextPage(page.Links?.Next, next);
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
