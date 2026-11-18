using SpotifyToTidal;
using Xunit;

public sealed class SyncEngineTests
{
    private static readonly Track First = new("from-1", "Blue Song", "Singer", "US1234567890");
    private static readonly Track Dest = First with { Id = "to-1" };
    private sealed class FakeLibrary(string name) : ITrackLibrary
    {
        public string Name => name;
        public List<Track> Favorites { get; } = [];
        public List<Track> Catalog { get; } = [];
        public List<string> Added { get; } = [];
        public bool ThrowOnSearch { get; set; }
        public bool ThrowOnAdd { get; set; }
        public bool CancelOnSearch { get; set; }
        public Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Track>>(Favorites.ToArray());
        public Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default)
        {
            if (CancelOnSearch) throw new OperationCanceledException();
            if (ThrowOnSearch) throw new HttpRequestException("search unavailable");
            return Task.FromResult<IReadOnlyList<Track>>(Catalog.Where(t =>
                string.Equals(t.Isrc, isrc, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        public Task AddFavorite(string trackId, CancellationToken ct = default)
        {
            if (ThrowOnAdd) throw new InvalidOperationException("write unavailable");
            Added.Add(trackId);
            return Task.CompletedTask;
        }
    }

    private static (FakeLibrary Source, FakeLibrary Target) Pair()
    {
        var source = new FakeLibrary("source");
        var target = new FakeLibrary("target");
        source.Favorites.Add(First);
        target.Catalog.Add(Dest);
        return (source, target);
    }

    [Fact]
    public async Task Dry_run_never_calls_write()
    {
        var (source, target) = Pair();
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, true);
        Assert.Equal("would-add", Assert.Single(rows).Status);
        Assert.Empty(target.Added);
    }

    [Fact]
    public async Task Successful_transfer_and_repeated_favorites_are_idempotent()
    {
        var (source, target) = Pair();
        source.Favorites.Add(First);
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Single(target.Added);
        Assert.Equal("added", Assert.Single(rows).Status);
    }

    [Fact]
    public async Task Destination_with_equivalent_recording_is_skipped()
    {
        var (source, target) = Pair();
        target.Favorites.Add(Dest);
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("already-present", Assert.Single(rows).Status);
        Assert.Empty(target.Added);
    }

    [Fact]
    public async Task Destination_ID_already_favorited_never_written_twice()
    {
        var (source, target) = Pair();
        target.Favorites.Add(Dest with { Isrc = null });
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("already-present", Assert.Single(rows).Status);
        Assert.Equal("to-1", rows[0].DestinationId);
        Assert.Empty(target.Added);
    }

    [Fact]
    public async Task Missing_isrc_is_reported_without_search()
    {
        var (source, target) = Pair();
        source.Favorites[0] = First with { Isrc = null };
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("unmatched", Assert.Single(rows).Status);
        Assert.Contains("Missing ISRC", rows[0].Note);
        Assert.Empty(target.Added);
    }

    [Fact]
    public async Task No_catalog_candidate_is_reported()
    {
        var (source, target) = Pair();
        target.Catalog.Clear();
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("unmatched", Assert.Single(rows).Status);
        Assert.Contains("No exact", rows[0].Note);
    }

    [Fact]
    public async Task Duplicate_candidate_ids_are_treated_as_one_match()
    {
        var (source, target) = Pair();
        target.Catalog.Add(Dest);
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("added", Assert.Single(rows).Status);
        Assert.Single(target.Added);
    }

    [Fact]
    public async Task Multiple_catalog_recordings_are_ambiguous()
    {
        var (source, target) = Pair();
        target.Catalog.Add(Dest with { Id = "other" });
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("unmatched", Assert.Single(rows).Status);
        Assert.Contains("Multiple", rows[0].Note);
    }

    [Fact]
    public async Task Search_errors_are_reported_and_do_not_abort()
    {
        var (source, target) = Pair();
        target.ThrowOnSearch = true;
        var stderr = Console.Error;
        using var output = new StringWriter();
        IReadOnlyList<SyncResult> rows;
        try
        {
            Console.SetError(output);
            rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        }
        finally { Console.SetError(stderr); }
        Assert.Equal("error", Assert.Single(rows).Status);
        Assert.Contains("search unavailable", rows[0].Note);
        Assert.Contains("Error: spotify-to-tidal track from-1: search unavailable", output.ToString());
    }

    [Fact]
    public async Task Write_errors_are_reported()
    {
        var (source, target) = Pair();
        target.ThrowOnAdd = true;
        var rows = await SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false);
        Assert.Equal("error", Assert.Single(rows).Status);
        Assert.Contains("write unavailable", rows[0].Note);
    }

    [Fact]
    public async Task Cancellation_is_propagated_rather_than_reported_as_error()
    {
        var (source, target) = Pair();
        target.CancelOnSearch = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false));
    }

    [Fact]
    public async Task Already_cancelled_token_is_propagated()
    {
        var (source, target) = Pair();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncEngine.Run(source, target, SyncDirection.SpotifyToTidal, false, cts.Token));
    }

    [Fact]
    public async Task Empty_libraries_yield_no_rows()
    {
        var spotify = new FakeLibrary("Spotify");
        var tidal = new FakeLibrary("TIDAL");
        Assert.Empty(await SyncEngine.Run(spotify, tidal, SyncDirection.Both, false));
    }

    [Fact]
    public void Normalization_handles_null_whitespace_and_punctuation()
    {
        Assert.Equal("", TrackMatcher.Normalize(null));
        Assert.Equal("", TrackMatcher.Normalize("   "));
        Assert.Equal("hello42", TrackMatcher.Normalize("Héllo - 42!"));
    }

    [Fact]
    public void Isrc_must_be_present_even_when_title_and_artist_match()
    {
        Assert.False(TrackMatcher.SameRecording(First with { Isrc = "" }, Dest));
    }

    [Fact]
    public void Csv_report_handles_empty_data_and_null_values()
    {
        Assert.Single(CsvReport.Serialize([]).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var row = new SyncResult("X", "1", "", "", null, "unmatched", null, "");
        Assert.Contains("\"\"", CsvReport.Serialize([row]));
    }
}
