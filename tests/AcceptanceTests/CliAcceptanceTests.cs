using System.Text.Json;
using SpotifyToTidal;
using Xunit;

public sealed class CliAcceptanceTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "SpotifyToTidal-Tests-" + Guid.NewGuid().ToString("N"));

    private static async Task WriteCredentials(string path, bool tokens = true, bool expired = false,
        string spotifyClientId = "spotify-test-client")
    {
        Directory.CreateDirectory(path);
        var settings = new AppSettings(spotifyClientId, "tidal-test-client",
            "http://127.0.0.1:8765/callback/", "http://127.0.0.1:8766/callback/", "US");
        await File.WriteAllTextAsync(Path.Combine(path, "config.json"),
            JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings));
        if (tokens)
        {
            var time = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1);
            await File.WriteAllTextAsync(Path.Combine(path, "spotify-token.json"),
                JsonSerializer.Serialize(new OAuthToken("spotify-test", null, time),
                    AppJsonContext.Default.OAuthToken));
            await File.WriteAllTextAsync(Path.Combine(path, "tidal-token.json"),
                JsonSerializer.Serialize(new OAuthToken("tidal-test", null, time),
                    AppJsonContext.Default.OAuthToken));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("sync --help")]
    [InlineData("sync -h")]
    public async Task Help_shows_all_supported_sync_options_without_credentials(string command)
    {
        string path = TempDirectory();
        string[] args = command.Length == 0 ? [] : command.Split(' ');
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await Program.Run(args, path));
        }
        finally
        {
            Console.SetOut(original);
        }

        var help = output.ToString();
        Assert.Contains("--prune-unavailable-tidal", help);
        Assert.Contains("--dry-run", help);
        Assert.Contains("--max-concurrency", help);
        Assert.Contains("--direction", help);
        Assert.Contains("SpotifyToTidal sync --dry-run --prune-unavailable-tidal", help);
        Assert.Contains("destructive; disabled by default", help);
        Assert.False(File.Exists(Path.Combine(path, "config.json")));
    }

    [Fact]
    public async Task Command_line_help_and_reports_have_no_configuration_dependency()
    {
        Assert.Equal(0, await Program.Main([]));
        string path = TempDirectory();
        Assert.Equal(0, await Program.Run(["report"], path));
        Assert.False(File.Exists(Path.Combine(path, "config.json")));
    }

    [Fact]
    public async Task Missing_or_null_configuration_reports_error()
    {
        string path = TempDirectory();
        Assert.Equal(1, await Program.Run(["sync"], path));
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "config.json"), "null");
        Assert.Equal(1, await Program.Run(["sync"], path));
    }

    [Fact]
    public async Task Unknown_commands_and_unrecognized_direction_are_rejected()
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        Assert.Equal(2, await Program.Run(["frobnicate"], path));
        Assert.Equal(2, await Program.Run(["login", "unknown"], path));
        Assert.Equal(1, await Program.Run(["sync", "--direction", "invalid"], path));
        Assert.Equal(1, await Program.Run(["sync", "--direction"], path));
        Assert.Equal(1, await Program.Run(["sync", "--unknown-option"], path));
    }

    [Fact]
    public async Task Login_rejects_placeholder_client_id()
    {
        string path = TempDirectory();
        await WriteCredentials(path, spotifyClientId: "REPLACE_SPOTIFY_CLIENT_ID");
        Assert.Equal(1, await Program.Run(["login", "spotify"], path));
    }

    [Fact]
    public async Task Missing_or_expired_tokens_cannot_sync()
    {
        string path = TempDirectory();
        await WriteCredentials(path, tokens: false);
        using (var http = new HttpClient(new FakeMusicApi()))
        {
            Assert.Equal(1, await Program.Run(["sync"], path, http));
        }
        await WriteCredentials(path, expired: true);
        using var anotherHttp = new HttpClient(new FakeMusicApi());
        Assert.Equal(1, await Program.Run(["sync"], path, anotherHttp));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("spotify-to-tidal")]
    [InlineData("tidal-to-spotify")]
    public async Task One_way_and_two_way_CLI_commands_generate_CSV(string direction)
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        var fake = new FakeMusicApi();
        using var http = new HttpClient(fake);
        Assert.Equal(0, await Program.Run(["sync", "--direction", direction], path, http));
        var csv = await File.ReadAllTextAsync(Path.Combine(path, "report.csv"));
        Assert.Contains("Direction,SourceId", csv);
        Assert.Contains(direction == "both" ? "spotify-to-tidal" : direction, csv);
    }

    [Fact]
    public async Task Dry_run_writes_a_report_but_no_external_favorites()
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        var fake = new FakeMusicApi();
        using var http = new HttpClient(fake);
        Assert.Equal(0, await Program.Run(["sync", "--dry-run"], path, http));
        Assert.Equal(0, fake.SpotifyWrites);
        Assert.Equal(0, fake.TidalWrites);
        var csv = await File.ReadAllTextAsync(Path.Combine(path, "report.csv"));
        Assert.Contains("would-add", csv);
    }

    [Fact]
    public async Task Invalid_JSON_configuration_does_not_crash_process()
    {
        string path = TempDirectory();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "config.json"), "{ invalid");
        Assert.Equal(1, await Program.Run(["sync"], path));
    }
    [Fact]
    public async Task Http_write_failure_produces_a_report_and_nonzero_exit_code()
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        var fake = new FakeMusicApi { RejectTidalWrites = true };
        using var http = new HttpClient(fake);
        Assert.Equal(1, await Program.Run(["sync", "--direction", "spotify-to-tidal"], path, http));
        var report = await File.ReadAllTextAsync(Path.Combine(path, "report.csv"));
        Assert.Contains("write denied", report);
        Assert.Contains("error", report);
    }

    [Fact]
    public async Task Sync_cli_supports_bounded_workers_and_opt_in_prune_preview()
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        using var http = new HttpClient(new FakeMusicApi());
        Assert.Equal(0, await Program.Run(
            ["sync", "--dry-run", "--max-concurrency", "2", "--prune-unavailable-tidal"],
            path, http));
        Assert.True(File.Exists(Path.Combine(path, "report.csv")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("invalid")]
    public async Task Sync_rejects_out_of_bounds_worker_limits(string value)
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        Assert.Equal(1, await Program.Run(["sync", "--max-concurrency", value], path));
    }

    [Fact]
    public async Task Sync_rejects_missing_worker_limit()
    {
        string path = TempDirectory();
        await WriteCredentials(path);
        Assert.Equal(1, await Program.Run(["sync", "--max-concurrency"], path));
    }

}
