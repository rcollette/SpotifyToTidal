using System.Collections.Concurrent;
using SpotifyToTidal;
using Xunit;

public sealed class ParallelSyncTests
{
    private sealed class Library(string name) : ITrackLibrary
    {
        public string Name => name;
        public IReadOnlyList<Track> Favorites { get; set; } = [];
        public IReadOnlyList<Track> Catalog { get; set; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<Track>>>? Read { get; set; }
        public Func<string, CancellationToken, Task<IReadOnlyList<Track>>>? Search { get; set; }
        public Func<string, CancellationToken, Task>? Add { get; set; }
        public readonly ConcurrentBag<string> Written = [];

        public Task<IReadOnlyList<Track>> GetFavorites(CancellationToken ct = default) =>
            Read is null ? Task.FromResult(Favorites) : Read(ct);
        public Task<IReadOnlyList<Track>> SearchIsrc(string isrc, CancellationToken ct = default) =>
            Search is null ? Task.FromResult<IReadOnlyList<Track>>(
                Catalog.Where(t => t.Isrc == isrc).ToArray()) : Search(isrc, ct);
        public async Task AddFavorite(string trackId, CancellationToken ct = default)
        {
            if (Add is not null) await Add(trackId, ct);
            Written.Add(trackId);
        }
    }

    private static Track Song(int id, string prefix = "s") =>
        new(prefix + id, "Song " + id, "Singer", "USAAA" + id.ToString("D7"));

    [Fact]
    public async Task Both_library_snapshots_start_concurrently()
    {
        int started = 0;
        var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<Track>> Read(CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult(true);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return [];
        }
        var spotify = new Library("Spotify") { Read = Read };
        var tidal = new Library("TIDAL") { Read = Read };
        var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, true);
        Assert.Empty(rows);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task Parallel_searches_and_writes_never_exceed_configured_workers()
    {
        int active = 0, peak = 0;
        var gate = new object();
        var source = new Library("Spotify")
        {
            Favorites = Enumerable.Range(1, 9).Select(i => Song(i)).ToArray()
        };
        var destination = new Library("TIDAL")
        {
            Search = async (isrc, token) =>
            {
                int inFlight = Interlocked.Increment(ref active);
                lock (gate) peak = Math.Max(peak, inFlight);
                try
                {
                    await Task.Delay(30, token);
                    int id = int.Parse(isrc["USAAA".Length..]);
                    return [Song(id, "t")];
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            Add = async (_, token) => await Task.Delay(15, token)
        };
        var events = new ConcurrentBag<SyncProgress>();
        var rows = await SyncEngine.Run(source, destination,
            SyncDirection.SpotifyToTidal, false, maxConcurrency: 3,
            progress: events.Add);
        Assert.Equal(3, peak);
        Assert.Equal(9, rows.Count);
        Assert.All(rows, row => Assert.Equal("added", row.Status));
        Assert.Equal(9, destination.Written.Count);
        Assert.Contains(events, p => p.Stage == "Spotify → TIDAL" &&
            p.Completed == 9 && p.Total == 9);
        Assert.Contains(events, p => p.Stage == "Spotify favorites" &&
            p.Completed == 9 && p.Total == 9);
    }

    [Fact]
    public async Task Parallel_duplicate_matches_are_written_only_once()
    {
        var first = Song(1);
        var source = new Library("Spotify")
        {
            Favorites = [first, first with { Id = "second-spotify-id" }]
        };
        var destination = new Library("TIDAL")
        {
            Catalog = [first with { Id = "one-tidal-id" }],
            Add = async (_, token) => await Task.Delay(25, token)
        };
        var rows = await SyncEngine.Run(source, destination,
            SyncDirection.SpotifyToTidal, false, maxConcurrency: 2);
        Assert.Single(destination.Written);
        Assert.Single(rows, row => row.Status == "added");
        Assert.Single(rows, row => row.Status == "already-present");
    }

    [Fact]
    public async Task Failed_parallel_write_does_not_reserve_target_for_following_song()
    {
        int calls = 0;
        var song = Song(1);
        var source = new Library("Spotify")
        {
            Favorites = [song, song with { Id = "alternate" }]
        };
        var destination = new Library("TIDAL")
        {
            Catalog = [song with { Id = "target" }],
            Add = (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("temporary write failure");
                return Task.CompletedTask;
            }
        };
        var rows = await SyncEngine.Run(source, destination,
            SyncDirection.SpotifyToTidal, false, maxConcurrency: 2);
        Assert.Single(rows, row => row.Status == "error");
        Assert.Single(rows, row => row.Status == "added");
        Assert.Single(destination.Written);
    }

    [Fact]
    public async Task Both_directional_searches_overlap_and_results_remain_ordered()
    {
        int active = 0, peak = 0;
        var gate = new object();
        async Task<IReadOnlyList<Track>> Search(string isrc, CancellationToken token, string prefix)
        {
            int count = Interlocked.Increment(ref active);
            lock (gate) peak = Math.Max(peak, count);
            try
            {
                await Task.Delay(30, token);
                return [new Track(prefix + isrc, isrc, "Singer", isrc)];
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
        var spotify = new Library("Spotify")
        {
            Favorites = [new Track("s1", "ISRC-A", "Singer", "ISRC-A")],
            Search = (isrc, token) => Search(isrc, token, "s")
        };
        var tidal = new Library("TIDAL")
        {
            Favorites = [new Track("t1", "ISRC-B", "Singer", "ISRC-B")],
            Search = (isrc, token) => Search(isrc, token, "t")
        };
        var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.Both, true,
            maxConcurrency: 2);
        Assert.Equal(2, rows.Count);
        Assert.Equal("spotify-to-tidal", rows[0].Direction);
        Assert.Equal("tidal-to-spotify", rows[1].Direction);
        Assert.Equal(2, peak);
        Assert.All(rows, row => Assert.Equal("would-add", row.Status));
    }

    [Fact]
    public async Task Empty_direction_reports_zero_progress()
    {
        var events = new ConcurrentBag<SyncProgress>();
        var spotify = new Library("Spotify");
        var tidal = new Library("TIDAL");
        var rows = await SyncEngine.Run(spotify, tidal, SyncDirection.Both,
            true, progress: events.Add);
        Assert.Empty(rows);
        Assert.Contains(events, p => p.Stage == "Spotify → TIDAL" &&
            p.Completed == 0 && p.Total == 0);
        Assert.Contains(events, p => p.Stage == "TIDAL → Spotify" &&
            p.Completed == 0 && p.Total == 0);
    }

    [Fact]
    public async Task Concurrency_must_be_positive()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SyncEngine.Run(new Library("Spotify"), new Library("TIDAL"),
                SyncDirection.Both, true, maxConcurrency: 0));
    }
}
