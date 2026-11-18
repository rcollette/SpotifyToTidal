using System.Collections.Concurrent;
using System.Text;

namespace SpotifyToTidal;

public sealed record Track(string Id, string Title, string Artist, string? Isrc);
public sealed record SyncResult(string Direction, string SourceId, string Title, string Artist,
    string? Isrc, string Status, string? DestinationId, string Note);

public interface ITrackLibrary
{
    string Name { get; }
    Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default);
    Task AddFavorite(string trackId, CancellationToken ct = default);
}

public enum SyncDirection { Both, SpotifyToTidal, TidalToSpotify }

public sealed record SyncProgress(string Stage, int Completed, int Total);

/// <summary>
/// Single synchronized owner of console progress output. Worker notifications
/// may arrive in any order; printed counts never move backwards or interleave.
/// </summary>
public sealed class SyncConsoleProgress(TextWriter output)
{
    readonly object gate = new();
    readonly Dictionary<string, int> lastReported = new(StringComparer.Ordinal);

    public void Report(SyncProgress update)
    {
        lock (gate)
        {
            var prior = lastReported.GetValueOrDefault(update.Stage, -1);
            if (update.Completed <= prior) return;
            var interval = Math.Max(1, update.Total / 20);
            if (update.Completed != 0 && update.Completed != update.Total &&
                update.Completed != 1 && update.Completed - prior < interval) return;
            output.WriteLine(update.Stage + ": " + update.Completed + "/" +
                update.Total + " tracks processed");
            lastReported[update.Stage] = update.Completed;
        }
    }
}

public static class TrackMatcher
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Normalize(NormalizationForm.FormKD)
            .Where(c => char.IsLetterOrDigit(c))
            .Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    public static bool SameRecording(Track source, Track candidate) =>
        !string.IsNullOrWhiteSpace(source.Isrc) &&
        string.Equals(source.Isrc, candidate.Isrc, StringComparison.OrdinalIgnoreCase) &&
        Normalize(source.Title) == Normalize(candidate.Title) &&
        !string.IsNullOrWhiteSpace(source.Artist) &&
        Normalize(source.Artist) == Normalize(candidate.Artist);

    public static (Track? Match, string Reason) UniqueMatch(Track source, IEnumerable<Track> candidates)
    {
        if (string.IsNullOrWhiteSpace(source.Isrc))
            return (null, "Missing ISRC; manual review required");
        var matches = candidates.Where(c => SameRecording(source, c))
            .DistinctBy(c => c.Id).ToArray();
        return matches.Length switch
        {
            1 => (matches[0], "Exact ISRC, title and artist"),
            0 => (null, "No exact ISRC/title/artist match"),
            _ => (null, "Multiple matching recordings; manual review required")
        };
    }
}

public static class SyncEngine
{
    public static async Task<IReadOnlyList<SyncResult>> Run(
        ITrackLibrary spotify, ITrackLibrary tidal, SyncDirection direction,
        bool dryRun, CancellationToken ct = default, int maxConcurrency = 5,
        Action<SyncProgress>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ct.ThrowIfCancellationRequested();

        // Both independent snapshots must complete before any writes begin.
        // This preserves the pre-sync contents in both directions.
        var spotifyTask = spotify.GetFavorites(ct);
        var tidalTask = tidal.GetFavorites(ct);
        await Task.WhenAll(spotifyTask, tidalTask);
        var spotifyFavorites = spotifyTask.Result.ToArray();
        var tidalFavorites = tidalTask.Result.ToArray();
        progress?.Invoke(new SyncProgress("Spotify favorites", spotifyFavorites.Length, spotifyFavorites.Length));
        progress?.Invoke(new SyncProgress("TIDAL favorites", tidalFavorites.Length, tidalFavorites.Length));

        async Task<SyncResult[]> Transfer(ITrackLibrary to, IReadOnlyList<Track> source,
            IReadOnlyList<Track> destination, string name)
        {
            var songs = source.DistinctBy(s => s.Id).ToArray();
            var rows = new SyncResult[songs.Length];
            var completed = 0;
            // A per-ID gate prevents two source records from adding the same
            // destination favorite concurrently. A failed add is not reserved.
            var locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
            var added = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            await Parallel.ForEachAsync(Enumerable.Range(0, songs.Length),
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                async (index, token) =>
                {
                    var song = songs[index];
                    try
                    {
                        if (destination.Any(existing => TrackMatcher.SameRecording(song, existing)))
                        {
                            rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                "already-present", null, "Already favorited");
                        }
                        else if (string.IsNullOrWhiteSpace(song.Isrc))
                        {
                            rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                "unmatched", null, "Missing ISRC; manual review required");
                        }
                        else
                        {
                            var candidates = await to.SearchIsrc(song.Isrc, token);
                            var (match, note) = TrackMatcher.UniqueMatch(song, candidates);
                            if (match is null)
                            {
                                rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                    "unmatched", null, note);
                            }
                            else if (destination.Any(t => t.Id == match.Id))
                            {
                                rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                    "already-present", match.Id, "Destination ID already favorited");
                            }
                            else if (dryRun)
                            {
                                rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                    "would-add", match.Id, note);
                            }
                            else
                            {
                                var gate = locks.GetOrAdd(match.Id, _ => new SemaphoreSlim(1, 1));
                                await gate.WaitAsync(token);
                                try
                                {
                                    if (added.ContainsKey(match.Id))
                                    {
                                        rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                            "already-present", match.Id, "Destination ID already favorited");
                                    }
                                    else
                                    {
                                        await to.AddFavorite(match.Id, token);
                                        added.TryAdd(match.Id, 0);
                                        rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                                            "added", match.Id, note);
                                    }
                                }
                                finally
                                {
                                    gate.Release();
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        rows[index] = new(name, song.Id, song.Title, song.Artist, song.Isrc,
                            "error", null, ex.Message);
                    }
                    finally
                    {
                        progress?.Invoke(new SyncProgress(name == "spotify-to-tidal"
                            ? "Spotify → TIDAL" : "TIDAL → Spotify",
                            Interlocked.Increment(ref completed), songs.Length));
                    }
                });
            if (songs.Length == 0)
                progress?.Invoke(new SyncProgress(name == "spotify-to-tidal"
                    ? "Spotify → TIDAL" : "TIDAL → Spotify", 0, 0));
            return rows;
        }

        // Directional jobs are independent after the snapshots. Their outputs
        // are returned in stable source order, even when HTTP calls finish out of order.
        var outbound = direction is SyncDirection.Both or SyncDirection.SpotifyToTidal
            ? Transfer(tidal, spotifyFavorites, tidalFavorites, "spotify-to-tidal")
            : Task.FromResult(Array.Empty<SyncResult>());
        var inbound = direction is SyncDirection.Both or SyncDirection.TidalToSpotify
            ? Transfer(spotify, tidalFavorites, spotifyFavorites, "tidal-to-spotify")
            : Task.FromResult(Array.Empty<SyncResult>());
        await Task.WhenAll(outbound, inbound);
        return [.. outbound.Result, .. inbound.Result];
    }
}

public static class CsvReport
{
    public static string Serialize(IEnumerable<SyncResult> results)
    {
        static string Quote(string? s) => "\"" + (s ?? "")
            .Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        var sb = new StringBuilder("Direction,SourceId,Title,Artist,ISRC,Status,DestinationId,Note\n");
        foreach (var r in results)
        {
            sb.AppendJoin(',', new[] { r.Direction, r.SourceId, r.Title, r.Artist, r.Isrc,
                r.Status, r.DestinationId, r.Note }.Select(Quote));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
