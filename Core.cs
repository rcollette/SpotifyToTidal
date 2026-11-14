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
        bool dryRun, CancellationToken ct = default)
    {
        // Snapshot BOTH libraries before any writes. Only additions are supported.
        var spotifyFavorites = (await spotify.GetFavorites(ct)).ToList();
        var tidalFavorites = (await tidal.GetFavorites(ct)).ToList();
        var results = new List<SyncResult>();

        async Task Transfer(ITrackLibrary from, ITrackLibrary to, IReadOnlyList<Track> source,
            List<Track> destination, string name)
        {
            foreach (var song in source.DistinctBy(s => s.Id))
            {
                ct.ThrowIfCancellationRequested();
                if (destination.Any(existing => TrackMatcher.SameRecording(song, existing)))
                {
                    results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                        "already-present", null, "Already favorited"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(song.Isrc))
                {
                    results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                        "unmatched", null, "Missing ISRC; manual review required"));
                    continue;
                }

                try
                {
                    var (match, note) = TrackMatcher.UniqueMatch(song, await to.SearchIsrc(song.Isrc, ct));
                    if (match is null)
                    {
                        results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                            "unmatched", null, note));
                    }
                    else if (destination.Any(t => t.Id == match.Id))
                    {
                        results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                            "already-present", match.Id, "Destination ID already favorited"));
                    }
                    else if (dryRun)
                    {
                        results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                            "would-add", match.Id, note));
                    }
                    else
                    {
                        await to.AddFavorite(match.Id, ct);
                        destination.Add(match);
                        results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                            "added", match.Id, note));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new(name, song.Id, song.Title, song.Artist, song.Isrc,
                        "error", null, ex.Message));
                }
            }
        }

        if (direction is SyncDirection.Both or SyncDirection.SpotifyToTidal)
            await Transfer(spotify, tidal, spotifyFavorites, tidalFavorites, "spotify-to-tidal");
        if (direction is SyncDirection.Both or SyncDirection.TidalToSpotify)
            await Transfer(tidal, spotify, tidalFavorites, spotifyFavorites, "tidal-to-spotify");
        return results;
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
