using SpotifyToTidal;
using Xunit;

public class TrackUnitTests
{
    static void Assert(bool value, string message = "Assertion failed")
    {
        if (!value) throw new Exception(message);
    }
    [Fact]
    public void Track_matching_and_CSV_cases()
    {
        void Check(string name, Action test) => test();
        Track a = new("spotify-1", "A Song (Live)", "Beyoncé", "USAAA0000001");
        Check("normalization handles case, punctuation and accents", () =>
            Assert(TrackMatcher.Normalize("  BéYoncé! ") == "beyonce"));
        Check("exact ISRC/title/artist match accepts another provider ID", () =>
            Assert(TrackMatcher.SameRecording(a, new Track("tidal-42", "A SONG (LIVE)", "Beyonce", "usaaa0000001"))));
        Check("same ISRC but different title is rejected", () =>
            Assert(!TrackMatcher.SameRecording(a, a with { Title = "A Song (Studio)" })));
        Check("same ISRC/title but different artist is rejected", () =>
            Assert(!TrackMatcher.SameRecording(a, a with { Artist = "Some Other Artist" })));
        Check("missing ISRC cannot match", () =>
            Assert(!TrackMatcher.SameRecording(a with { Isrc = null }, a)));
        Check("missing artist cannot match", () =>
            Assert(!TrackMatcher.SameRecording(a with { Artist = "" }, a)));
        Check("unique candidate chosen", () =>
        {
            var (match, _) = TrackMatcher.UniqueMatch(a, new[] {
                a with { Id = "tidal-correct" }, a with { Id = "tidal-wrong", Title = "Different Song" }
            });
            Assert(match?.Id == "tidal-correct");
        });
        Check("duplicated search result does not create ambiguity", () =>
        {
            var candidate = a with { Id = "tidal-correct" };
            Assert(TrackMatcher.UniqueMatch(a, new[] { candidate, candidate }).Match?.Id == candidate.Id);
        });
        Check("multiple matching provider IDs remain ambiguous", () =>
        {
            var (match, reason) = TrackMatcher.UniqueMatch(a, new[] {
                a with { Id = "tidal-1" }, a with { Id = "tidal-2" }
            });
            Assert(match is null && reason.Contains("Multiple"));
        });
        Check("missing ISRC produces manual-review decision", () =>
        {
            var (match, reason) = TrackMatcher.UniqueMatch(a with { Isrc = null }, Array.Empty<Track>());
            Assert(match is null && reason.Contains("Missing ISRC"));
        });
        Check("CSV quotes commas, double quotes and line breaks", () =>
        {
            var csv = CsvReport.Serialize(new[] {
                new SyncResult("spotify-to-tidal", "a", "Song, \"Part 2\"\nremix",
                    "Artist", "ISRC", "unmatched", null, "Not found")
            });
            Assert(csv.Contains("\"Song, \"\"Part 2\"\" remix\""), csv);
            Assert(csv.Split('\n').Length == 3, "Expected header, row, final newline");
        });
        Check("case-insensitive ISRC and title matching across providers", () =>
        {
            var candidate = a with { Id = "tidal-case", Isrc = "usaaa0000001", Title = "A SONG (LIVE)" };
            Assert(TrackMatcher.UniqueMatch(a, new[] { candidate }).Match?.Id == candidate.Id);
        });

    }
}
