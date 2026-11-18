using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SpotifyToTidal;
using Xunit;

public sealed class HttpAdapterTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> script = new();
        public readonly List<string> Requests = [];
        public void Push(Func<HttpRequestMessage, HttpResponseMessage> response) => script.Enqueue(response);
        public void Push(HttpResponseMessage response) => Push(_ => response);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.NotNull(request.RequestUri);
            Requests.Add(request.Method + " " + request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-access", request.Headers.Authorization?.Parameter);
            if (script.Count == 0) throw new Exception("Unexpected request: " + request.RequestUri);
            return Task.FromResult(script.Dequeue()(request));
        }
    }

    private static HttpResponseMessage Response<T>(T dto, JsonTypeInfo<T> type, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(dto, type),
            Encoding.UTF8, "application/json") };
    private static SpotifyTrackDto SpotifyTrack(string? id, string? title = "Song", string? artist = "Singer",
        string? isrc = "USAAA0000001", bool local = false) =>
        new() { Id = id, Name = title, Artists = artist is null ? [] : [new SpotifyArtistDto { Name = artist }],
            ExternalIds = new SpotifyExternalIdsDto { Isrc = isrc }, IsLocal = local };
    private static TidalTrackDto TidalTrack(string? id, string? isrc = "USAAA0000001",
        string? artistFallback = null, bool relationship = true) =>
        new() { Id = id, Type = "tracks",
            Attributes = new TidalTrackAttributesDto { Title = "Song", Isrc = isrc, Artist = artistFallback },
            Relationships = relationship ? new TidalRelationshipsDto {
                Artists = new TidalArtistRelationshipDto {
                    Data = [new TidalResourceDto { Id = "artist", Type = "artists" }] } } : null };
    private static TidalArtistDto TidalArtist() => new()
    {
        Id = "artist", Type = "artists", Attributes = new TidalArtistAttributesDto { Name = "Singer" }
    };
    private static SpotifyLibrary Spotify(ScriptedHandler h, Func<TimeSpan, CancellationToken, Task>? wait = null) =>
        new(new HttpClient(h), () => Task.FromResult("test-access"),
            new Uri("https://spotify.test/"), wait);
    private static TidalLibrary Tidal(ScriptedHandler h, Func<TimeSpan, CancellationToken, Task>? wait = null) =>
        new(new HttpClient(h), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), "US", wait, maxConcurrency: 1);

    [Fact]
    public async Task Spotify_saved_tracks_are_paginated_deduplicated_and_skip_invalid_rows()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new SpotifySavedTracksPage {
            Items = [
                new SpotifySavedItem { Track = SpotifyTrack("id-1") },
                new SpotifySavedItem { Track = SpotifyTrack("local", local: true) },
                new SpotifySavedItem { Track = SpotifyTrack(null) },
                new SpotifySavedItem { Track = null }
            ], Next = "/v1/me/tracks?page=2"
        }, AppJsonContext.Default.SpotifySavedTracksPage));
        h.Push(Response(new SpotifySavedTracksPage {
            Items = [
                new SpotifySavedItem { Track = SpotifyTrack("id-1") },
                new SpotifySavedItem { Track = SpotifyTrack("id-2", artist:null) }
            ], Next = "/v1/me/tracks?page=2"
        }, AppJsonContext.Default.SpotifySavedTracksPage));
        var list = await Spotify(h).GetFavorites();
        Assert.Equal(2, list.Count);
        Assert.Equal("id-2", list[1].Id);
        Assert.Equal("", list[1].Artist);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Spotify_isrc_search_paginates_and_filters_inconsistent_results()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new SpotifySearchResponse { Tracks = new SpotifyTrackPage {
            Items = Enumerable.Range(0,10).Select(i =>
                SpotifyTrack("id-" + i)).ToArray() } },
            AppJsonContext.Default.SpotifySearchResponse));
        h.Push(Response(new SpotifySearchResponse { Tracks = new SpotifyTrackPage {
            Items = [ SpotifyTrack("id-10"), SpotifyTrack("bad", isrc:"OTHER") ] } },
            AppJsonContext.Default.SpotifySearchResponse));
        var list = await Spotify(h).SearchIsrc("USAAA0000001");
        Assert.Equal(11, list.Count);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("offset=10", h.Requests[1]);
        Assert.DoesNotContain(list, t => t.Id == "bad");
    }

    [Fact]
    public async Task Spotify_handles_missing_track_search_page()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new SpotifySearchResponse(), AppJsonContext.Default.SpotifySearchResponse));
        Assert.Empty(await Spotify(h).SearchIsrc("UNKNOWN"));
    }

    [Fact]
    public async Task Spotify_search_skips_invalid_tracks_and_duplicates()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new SpotifySearchResponse { Tracks = new SpotifyTrackPage {
            Items = [SpotifyTrack("good"), SpotifyTrack("good"), SpotifyTrack(null), SpotifyTrack("local", local:true)]
        } }, AppJsonContext.Default.SpotifySearchResponse));
        Assert.Equal("good", Assert.Single(await Spotify(h).SearchIsrc("USAAA0000001")).Id);
    }

    [Fact]
    public async Task Tidal_collection_pagination_loads_track_details_and_skips_missing_ids()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalCollectionPage {
            Data = [new TidalResourceDto { Id = null }, new TidalResourceDto { Id = "tid-1" }],
            Links = new TidalLinksDto { Next = "/v2/favorites?page=2" }
        }, AppJsonContext.Default.TidalCollectionPage));
        h.Push(Response(new TidalCollectionPage {
            Data = [new TidalResourceDto { Id = "tid-1" }, new TidalResourceDto { Id = "tid-2" }],
            Links = new TidalLinksDto { Next = "/v2/favorites?page=2" }
        }, AppJsonContext.Default.TidalCollectionPage));
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("tid-1"),
                TidalTrack("tid-2", relationship:false, artistFallback:"Fallback Artist")],
            Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        var list = await Tidal(h).GetFavorites();
        Assert.Equal(2, list.Count);
        Assert.Equal("Singer", list[0].Artist);
        Assert.Equal("Fallback Artist", list[1].Artist);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task Tidal_search_filters_ISRC_deduplicates_and_respects_pagination()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("tid-1"), TidalTrack("ignored", isrc:"OTHER")],
            Included = [TidalArtist()],
            Links = new TidalLinksDto { Next = "/v2/search?page=2" }
        }, AppJsonContext.Default.TidalTrackPage));
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("tid-1"), TidalTrack(null), TidalTrack("tid-2")],
            Included = [TidalArtist()],
            Links = new TidalLinksDto { Next = "/v2/search?page=2" }
        }, AppJsonContext.Default.TidalTrackPage));
        var list = await Tidal(h).SearchIsrc("USAAA0000001");
        Assert.Equal(2, list.Count);
        Assert.Equal("tid-2", list[1].Id);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Tidal_missing_artist_relationship_returns_blank_artist_safely()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("track", relationship:false)],
            Included = []
        }, AppJsonContext.Default.TidalTrackPage));
        var song = Assert.Single(await Tidal(h).SearchIsrc("USAAA0000001"));
        Assert.Equal("", song.Artist);
    }

    [Fact]
    public async Task Tidal_get_sends_JSON_API_Accept_header()
    {
        var handler = new ScriptedHandler();
        handler.Push(request =>
        {
            Assert.Equal("application/vnd.api+json", Assert.Single(request.Headers.Accept).ToString());
            return Response(new TidalTrackPage(), AppJsonContext.Default.TidalTrackPage);
        });
        Assert.Empty(await Tidal(handler).SearchIsrc("USAAA0000001"));
    }

    [Fact]
    public async Task Tidal_post_sends_JSON_API_media_type_without_charset()
    {
        var handler = new ScriptedHandler();
        handler.Push(request =>
        {
            Assert.Equal("application/vnd.api+json", Assert.Single(request.Headers.Accept).ToString());
            Assert.Equal("application/vnd.api+json", request.Content?.Headers.ContentType?.ToString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Tidal(handler).AddFavorite("tidal-track-id");
    }

    [Fact]
    public async Task Tidal_add_serializes_a_static_request_type()
    {
        var h = new ScriptedHandler();
        h.Push(req => {
            var json = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var add = JsonSerializer.Deserialize(json, AppJsonContext.Default.TidalCollectionWriteRequest);
            Assert.Equal("xyz", Assert.Single(add!.Data).Id);
            Assert.Equal("tracks", add.Data[0].Type);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Tidal(h).AddFavorite("xyz");
    }

    [Fact]
    public async Task Spotify_add_uses_correct_uri_and_does_not_need_a_response_body()
    {
        var h = new ScriptedHandler();
        h.Push(req => {
            Assert.Contains("spotify%3Atrack%3Atrack-1", req.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Spotify(h).AddFavorite("track-1");
    }

    [Fact]
    public async Task Too_many_requests_honors_retry_after_with_sixty_second_cap()
    {
        var h = new ScriptedHandler();
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
        h.Push(retry);
        h.Push(Response(new SpotifySavedTracksPage(), AppJsonContext.Default.SpotifySavedTracksPage));
        List<TimeSpan> waits = [];
        var lib = Spotify(h, (delay, ct) => { waits.Add(delay); return Task.CompletedTask; });
        var original = Console.Error;
        using var log = new StringWriter();
        try
        {
            Console.SetError(log);
            Assert.Empty(await lib.GetFavorites());
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(waits));
        Assert.Contains("Spotify HTTP 429", log.ToString());
        Assert.Contains("GET /v1/me/tracks", log.ToString());
        Assert.Contains("attempt 1/5", log.ToString());
        Assert.Contains("server Retry-After=180s", log.ToString());
        Assert.Contains("waiting 60s (60s maximum)", log.ToString());
        Assert.Contains("before attempt 2/5", log.ToString());
    }

    [Fact]
    public async Task HTTP_429_without_retry_after_logs_exponential_schedule_and_actual_delays()
    {
        var handler = new ScriptedHandler();
        handler.Push(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        handler.Push(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        handler.Push(Response(new SpotifySearchResponse(), AppJsonContext.Default.SpotifySearchResponse));
        List<TimeSpan> delays = [];
        var library = Spotify(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var original = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            Assert.Empty(await library.SearchIsrc("TEST-ISRC-UNLOGGED"));
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
        var log = output.ToString();
        Assert.Contains("Spotify HTTP 429 rate limited GET /v1/search", log);
        Assert.Contains("attempt 1/5", log);
        Assert.Contains("attempt 2/5", log);
        Assert.Contains("exponential backoff (2, 4, 8, 16s", log);
        Assert.Contains("waiting 2s", log);
        Assert.Contains("waiting 4s", log);
        Assert.DoesNotContain("TEST-ISRC-UNLOGGED", log);
    }

    [Fact]
    public async Task Final_HTTP_429_is_logged_as_exhausted_without_another_delay()
    {
        var handler = new ScriptedHandler();
        for (int i = 0; i < 5; i++)
            handler.Push(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        List<TimeSpan> waits = [];
        var library = Tidal(handler, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        var original = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => library.GetFavorites());
            Assert.Contains("TIDAL returned 429 after retries", ex.Message);
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(new[] { 2.0, 4.0, 8.0, 16.0 }, waits.Select(x => x.TotalSeconds));
        var log = output.ToString();
        Assert.Equal(5, log.Split("HTTP 429", StringSplitOptions.None).Length - 1);
        Assert.Contains("attempt 5/5", log);
        Assert.Contains("No more retries after 4 backoff waits", log);
        Assert.Contains("policy: Retry-After seconds (max 60s)", log);
    }

    [Fact]
    public async Task Service_unavailable_uses_exponential_retry_without_retry_after()
    {
        var h = new ScriptedHandler();
        h.Push(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        h.Push(Response(new SpotifySavedTracksPage(), AppJsonContext.Default.SpotifySavedTracksPage));
        List<TimeSpan> waits = [];
        Assert.Empty(await Spotify(h, (delay, ct) => {
            waits.Add(delay); return Task.CompletedTask;
        }).GetFavorites());
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(waits));
    }

    [Fact]
    public async Task Five_retryable_errors_fail_with_provider_context()
    {
        var h = new ScriptedHandler();
        for (int i=0;i<5;i++) h.Push(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        int waits = 0;
        var lib = Tidal(h, (delay, ct) => { waits++; return Task.CompletedTask; });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => lib.GetFavorites());
        Assert.Contains("TIDAL returned 500", ex.Message);
        Assert.Equal(4, waits);
        Assert.Equal(5, h.Requests.Count);
    }

    [Fact]
    public async Task Nonretryable_http_error_is_returned_with_bounded_message()
    {
        var h = new ScriptedHandler();
        h.Push(new HttpResponseMessage(HttpStatusCode.Forbidden) {
            Content = new StringContent(new string('x', 500))
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Spotify(h).GetFavorites());
        Assert.Contains("Spotify returned 403", ex.Message);
        Assert.Contains(new string('x', 400), ex.Message);
        Assert.DoesNotContain(new string('x', 401), ex.Message);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Null_JSON_document_is_rejected()
    {
        var h = new ScriptedHandler();
        h.Push(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") });
        await Assert.ThrowsAsync<JsonException>(() => Spotify(h).GetFavorites());
    }
    [Fact]
    public async Task Default_retry_delay_is_used_when_no_delegate_is_provided()
    {
        var h = new ScriptedHandler();
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        h.Push(retry);
        h.Push(Response(new SpotifySavedTracksPage(), AppJsonContext.Default.SpotifySavedTracksPage));
        Assert.Empty(await Spotify(h).GetFavorites());
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Spotify_track_metadata_may_omit_title_artist_and_external_ids()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new SpotifySavedTracksPage {
            Items = [new SpotifySavedItem { Track = new SpotifyTrackDto {
                Id = "minimal", Name = null, Artists = [new SpotifyArtistDto { Name = null }],
                ExternalIds = null } }]
        }, AppJsonContext.Default.SpotifySavedTracksPage));
        var track = Assert.Single(await Spotify(h).GetFavorites());
        Assert.Equal("", track.Title);
        Assert.Equal("", track.Artist);
        Assert.Null(track.Isrc);
    }

    [Fact]
    public async Task Tidal_missing_track_data_and_links_are_safe()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalCollectionPage {
            Data = [new TidalResourceDto { Id = "track-1" }], Links = null
        }, AppJsonContext.Default.TidalCollectionPage));
        h.Push(Response(new TidalTrackDetail { Data = null }, AppJsonContext.Default.TidalTrackDetail));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Tidal(h).GetFavorites());
        Assert.Contains("Every TIDAL favorite track has missing or unavailable metadata", ex.Message);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Tidal_artist_matching_rejects_wrong_type_and_unrelated_artist_ids()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("first", artistFallback:"fallback")],
            Included = [
                new TidalArtistDto { Id = "artist", Type = "label",
                    Attributes = new TidalArtistAttributesDto { Name = "Incorrect type" } },
                new TidalArtistDto { Id = "unrelated", Type = "artists",
                    Attributes = new TidalArtistAttributesDto { Name = "Incorrect id" } }
            ]
        }, AppJsonContext.Default.TidalTrackPage));
        var song = Assert.Single(await Tidal(h).SearchIsrc("USAAA0000001"));
        Assert.Equal("fallback", song.Artist);
    }

    [Fact]
    public async Task Tidal_artist_relationship_can_be_null_even_if_relationships_exist()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [new TidalTrackDto {
                Id = "track", Relationships = new TidalRelationshipsDto { Artists = null },
                Attributes = new TidalTrackAttributesDto {
                    Title = "Song", Isrc = "USAAA0000001", Artist = "Local metadata" }
            } ]
        }, AppJsonContext.Default.TidalTrackPage));
        var song = Assert.Single(await Tidal(h).SearchIsrc("USAAA0000001"));
        Assert.Equal("Local metadata", song.Artist);
    }

    [Fact]
    public async Task Tidal_matched_artist_without_attributes_falls_back_to_track_artist()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [TidalTrack("track", artistFallback:"Fallback")],
            Included = [new TidalArtistDto { Id="artist", Type="artists", Attributes=null }]
        }, AppJsonContext.Default.TidalTrackPage));
        var song = Assert.Single(await Tidal(h).SearchIsrc("USAAA0000001"));
        Assert.Equal("Fallback", song.Artist);
    }

    [Fact]
    public async Task Tidal_track_without_attributes_produces_empty_metadata()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [new TidalTrackDto { Id = "track", Attributes = null, Relationships = null }]
        }, AppJsonContext.Default.TidalTrackPage));
        var songs = await Tidal(h).SearchIsrc("USAAA0000001");
        Assert.Empty(songs);
    }

    [Fact]
    public async Task Tidal_artist_relation_with_null_id_is_ignored()
    {
        var h = new ScriptedHandler();
        h.Push(Response(new TidalTrackPage {
            Data = [new TidalTrackDto {
                Id="track", Attributes = new TidalTrackAttributesDto {
                    Title="Song", Isrc="USAAA0000001", Artist="Fallback" },
                Relationships = new TidalRelationshipsDto {
                    Artists = new TidalArtistRelationshipDto {
                        Data = [new TidalResourceDto { Id = null }] }
                }
            }],
            Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        Assert.Equal("Fallback", Assert.Single(await Tidal(h).SearchIsrc("USAAA0000001")).Artist);
    }


    [Fact]
    public async Task Tidal_collection_cursor_link_relative_to_v2_root_preserves_api_prefix()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Links = new TidalLinksDto
            {
                Next = "/userCollectionTracks/me/relationships/items?page%5Bcursor%5D=NEXT"
            }
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalCollectionPage(), AppJsonContext.Default.TidalCollectionPage));
        Assert.Empty(await Tidal(handler).GetFavorites());
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("GET https://tidal.test/v2/userCollectionTracks/me/relationships/items?",
            handler.Requests[1]);
        Assert.Contains("NEXT", handler.Requests[1]);
    }

    [Fact]
    public async Task Tidal_track_search_cursor_only_link_retains_current_resource_path()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalTrackPage
        {
            Links = new TidalLinksDto { Next = "?page%5Bcursor%5D=CURSOR" }
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(Response(new TidalTrackPage(), AppJsonContext.Default.TidalTrackPage));
        Assert.Empty(await Tidal(handler).SearchIsrc("USAAA0000001"));
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("GET https://tidal.test/v2/tracks?page", handler.Requests[1]);
        Assert.Contains("CURSOR", handler.Requests[1]);
    }

    [Fact]
    public async Task Tidal_relative_cursor_without_leading_slash_still_uses_v2_root()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Links = new TidalLinksDto
            {
                Next = "userCollectionTracks/me/relationships/items?page%5Bcursor%5D=RELATIVE"
            }
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalCollectionPage(), AppJsonContext.Default.TidalCollectionPage));
        Assert.Empty(await Tidal(handler).GetFavorites());
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("GET https://tidal.test/v2/userCollectionTracks/me/relationships/items?",
            handler.Requests[1]);
        Assert.Contains("RELATIVE", handler.Requests[1]);
    }

    [Fact]
    public async Task Tidal_absolute_cursor_link_uses_supplied_api_url()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Links = new TidalLinksDto
            {
                Next = "https://tidal.test/v2/userCollectionTracks/me/relationships/items?page%5Bcursor%5D=LAST"
            }
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalCollectionPage(), AppJsonContext.Default.TidalCollectionPage));
        Assert.Empty(await Tidal(handler).GetFavorites());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/v2/userCollectionTracks/me/relationships/items?", handler.Requests[1]);
        Assert.Contains("LAST", handler.Requests[1]);
    }

    [Fact]
    public async Task Tidal_favorites_use_documented_collection_relationship_route_without_countryCode()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage(), AppJsonContext.Default.TidalCollectionPage));
        Assert.Empty(await Tidal(handler).GetFavorites());
        Assert.Equal("GET https://tidal.test/v2/userCollectionTracks/me/relationships/items",
            Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Tidal_collection_404_reports_request_and_app_permissions_without_tokens()
    {
        var handler = new ScriptedHandler();
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"errors":[{"code":"NOT_FOUND","detail":"Resource not found"}]}""")
        });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Tidal(handler).GetFavorites());
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("TIDAL returned 404", exception.Message);
        Assert.Contains("GET https://tidal.test/v2/userCollectionTracks/me/relationships/items", exception.Message);
        Assert.Contains("collection.read", exception.Message);
        Assert.Contains("login tidal", exception.Message);
        Assert.DoesNotContain("test-access", exception.Message);
    }

    [Fact]
    public async Task Tidal_collection_403_reports_request_but_does_not_apply_404_specific_message()
    {
        var handler = new ScriptedHandler();
        handler.Push(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("collection permission denied")
        });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Tidal(handler).GetFavorites());
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("TIDAL returned 403 for GET https://tidal.test/v2/userCollectionTracks/me/relationships/items", exception.Message);
        Assert.DoesNotContain("ask TIDAL developer support", exception.Message);
    }

    [Fact]
    public async Task Tidal_skips_one_retired_favorite_and_continues_loading_valid_tracks()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "476318120", Type = "tracks" },
                new TidalResourceDto { Id = "still-available", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("still-available")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"errors":[{"code":"NOT_FOUND","detail":"Resource not found"}]}""")
        });

        var song = Assert.Single(await Tidal(handler).GetFavorites());
        Assert.Equal("still-available", song.Id);
        Assert.Equal("Singer", song.Artist);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("GET https://tidal.test/v2/tracks?filter%5Bid%5D=",
            handler.Requests[1]);
        Assert.Contains("GET https://tidal.test/v2/tracks/476318120?countryCode=US",
            handler.Requests[2]);
    }

    [Fact]
    public async Task Tidal_track_detail_forbidden_still_aborts_collection_read()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [new TidalResourceDto { Id = "blocked", Type = "tracks" }]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("catalog access denied")
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Tidal(handler).GetFavorites());
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("GET https://tidal.test/v2/tracks/blocked", exception.Message);
    }

    [Fact]
    public async Task Tidal_track_detail_404_identifies_track_request_without_misdiagnosing_collection_scope()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [new TidalResourceDto { Id = "not-available", Type = "tracks" }]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"errors":[{"code":"NOT_FOUND"}]}""")
        });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Tidal(handler).GetFavorites());
        Assert.Contains("Every TIDAL favorite track has missing or unavailable metadata", exception.Message);
        Assert.Contains("config.json countryCode", exception.Message);
        Assert.DoesNotContain("ask TIDAL developer support", exception.Message);
        Assert.Equal(2, handler.Requests.Count);
    }


    private sealed class ConcurrentTidalMetadataHandler : HttpMessageHandler
    {
        readonly object gate = new();
        int active;
        public int Peak { get; private set; }
        public int BatchRequests;
        public int SingleRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath ==
                "/v2/userCollectionTracks/me/relationships/items")
                return Response(new TidalCollectionPage
                {
                    Data = Enumerable.Range(1, 45)
                        .Select(i => new TidalResourceDto
                        {
                            Id = "item-" + i, Type = "tracks"
                        }).ToArray()
                }, AppJsonContext.Default.TidalCollectionPage);

            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri.AbsolutePath.StartsWith("/v2/tracks/"))
            {
                Interlocked.Increment(ref SingleRequests);
                throw new Exception("Unexpected individual metadata request");
            }
            Assert.Equal("/v2/tracks", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref BatchRequests);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var idsParam = query.TrimStart('?').Split('&')
                .Single(p => p.StartsWith("filter[id]=", StringComparison.Ordinal));
            var ids = idsParam["filter[id]=".Length..].Split(',');
            Assert.InRange(ids.Length, 1, 20);
            Assert.Contains("include=artists", query);

            var current = Interlocked.Increment(ref active);
            lock (gate) Peak = Math.Max(Peak, current);
            try
            {
                await Task.Delay(30, ct);
                return Response(new TidalTrackPage
                {
                    Data = ids.Select(id => TidalTrack(id, artistFallback: "Singer",
                        relationship: false)).ToArray()
                }, AppJsonContext.Default.TidalTrackPage);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    [Fact]
    public async Task Tidal_track_details_run_concurrently_up_to_worker_limit()
    {
        var handler = new ConcurrentTidalMetadataHandler();
        var progress = new System.Collections.Concurrent.ConcurrentBag<SyncProgress>();
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 3, progress: progress.Add);
        var songs = await library.GetFavorites();
        Assert.Equal(3, handler.Peak);
        Assert.Equal(3, handler.BatchRequests);
        Assert.Equal(0, handler.SingleRequests);
        Assert.Equal(45, songs.Count);
        Assert.Equal("item-1", songs[0].Id);
        Assert.Equal("item-45", songs[44].Id);
        Assert.Contains(progress, p => p.Stage == "TIDAL metadata" &&
            p.Completed == 45 && p.Total == 45);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Tidal_batch_HTTP_error_falls_back_to_individual_track_details(
        HttpStatusCode status)
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "first", Type = "tracks" },
                new TidalResourceDto { Id = "second", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(new HttpResponseMessage(status));
        handler.Push(Response(new TidalTrackDetail
        {
            Data = TidalTrack("first"), Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackDetail));
        handler.Push(Response(new TidalTrackDetail
        {
            Data = TidalTrack("second"), Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackDetail));

        var tracks = await Tidal(handler).GetFavorites();
        Assert.Equal(2, tracks.Count);
        Assert.Equal("first", tracks[0].Id);
        Assert.Equal("second", tracks[1].Id);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("/v2/tracks?filter%5Bid%5D=", handler.Requests[1]);
        Assert.Contains("/v2/tracks/first?countryCode=US", handler.Requests[2]);
    }

    [Fact]
    public async Task Tidal_batch_omissions_are_verified_individually_before_being_skipped()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "first", Type = "tracks" },
                new TidalResourceDto { Id = "second", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("first")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(Response(new TidalTrackDetail
        {
            Data = TidalTrack("second"), Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackDetail));

        var tracks = await Tidal(handler).GetFavorites();
        Assert.Equal(2, tracks.Count);
        Assert.Equal("second", tracks[1].Id);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Tidal_batch_metadata_follows_cursor_and_ignores_null_resources()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "first", Type = "tracks" },
                new TidalResourceDto { Id = "second", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [null!, TidalTrack("first")], Included = [TidalArtist()],
            Links = new TidalLinksDto
            {
                Next = "?filter%5Bid%5D=first%2Csecond&countryCode=US&include=artists&page%5Bcursor%5D=NEXT"
            }
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("second")]
        }, AppJsonContext.Default.TidalTrackPage));

        var tracks = await Tidal(handler).GetFavorites();
        Assert.Equal(2, tracks.Count);
        Assert.Equal("Singer", tracks[1].Artist);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("NEXT", handler.Requests[2]);
    }

    [Fact]
    public async Task Tidal_null_track_entry_is_ignored_safely()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalTrackPage
        {
            Data = [null!]
        }, AppJsonContext.Default.TidalTrackPage));
        Assert.Empty(await Tidal(handler).SearchIsrc("USAAA0000001"));
    }

    [Fact]
    public async Task Prune_preview_can_run_without_a_progress_listener()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "missing", Type = "tracks" },
                new TidalResourceDto { Id = "available", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("available")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound));
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1,
            deleteUnavailable: true, dryRun: true);
        Assert.Equal("available", Assert.Single(await library.GetFavorites()).Id);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Tidal_metadata_404_can_be_pruned_only_when_explicitly_enabled()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "476318120", Type = "tracks" },
                new TidalResourceDto { Id = "valid", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("valid")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"errors":[{"code":"NOT_FOUND"}]}""")
        });
        handler.Push(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/v2/userCollectionTracks/me/relationships/items",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("application/vnd.api+json",
                request.Content!.Headers.ContentType?.ToString());
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var remove = JsonSerializer.Deserialize(body, AppJsonContext.Default.TidalCollectionWriteRequest);
            Assert.Equal("476318120", Assert.Single(remove!.Data).Id);
            Assert.Equal("tracks", remove.Data[0].Type);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var progress = new List<SyncProgress>();
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1,
            deleteUnavailable: true, progress: progress.Add);
        Assert.Equal("valid", Assert.Single(await library.GetFavorites()).Id);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains(progress, p => p.Stage == "TIDAL metadata" &&
            p.Completed == 2 && p.Total == 2);
        Assert.Contains(progress, p => p.Stage == "TIDAL cleanup" &&
            p.Completed == 1 && p.Total == 1);
    }

    [Fact]
    public async Task Pruning_dry_run_never_deletes_and_previews_count()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "missing", Type = "tracks" },
                new TidalResourceDto { Id = "ok", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("ok")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound));

        var progress = new List<SyncProgress>();
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1, deleteUnavailable: true,
            dryRun: true, progress: progress.Add);
        Assert.Equal("ok", Assert.Single(await library.GetFavorites()).Id);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("DELETE"));
        Assert.Contains(progress, p => p.Stage == "TIDAL cleanup preview" &&
            p.Completed == 1 && p.Total == 1);
    }

    [Fact]
    public async Task Missing_track_attributes_are_prunable_but_missing_isrc_is_not()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "without-metadata", Type = "tracks" },
                new TidalResourceDto { Id = "no-isrc", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [
                new TidalTrackDto { Id = "without-metadata", Attributes = null },
                TidalTrack("no-isrc", isrc: null)
            ], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(Response(new TidalTrackDetail
        {
            Data = new TidalTrackDto { Id = "without-metadata", Attributes = null }
        }, AppJsonContext.Default.TidalTrackDetail));
        handler.Push(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var deletion = JsonSerializer.Deserialize(body, AppJsonContext.Default.TidalCollectionWriteRequest);
            Assert.Equal("without-metadata", Assert.Single(deletion!.Data).Id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1, deleteUnavailable: true);
        var song = Assert.Single(await library.GetFavorites());
        Assert.Equal("no-isrc", song.Id);
        Assert.Null(song.Isrc);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Entirely_unavailable_collection_is_not_deleted_even_with_opt_in()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [new TidalResourceDto { Id = "unavailable", Type = "tracks" }]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound));
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1, deleteUnavailable: true);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => library.GetFavorites());
        Assert.Contains("refusing to treat the collection as empty", error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("DELETE"));
    }

    [Fact]
    public async Task Cleanup_delete_failure_is_not_silently_ignored()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage
        {
            Data = [
                new TidalResourceDto { Id = "missing", Type = "tracks" },
                new TidalResourceDto { Id = "valid", Type = "tracks" }
            ]
        }, AppJsonContext.Default.TidalCollectionPage));
        handler.Push(Response(new TidalTrackPage
        {
            Data = [TidalTrack("valid")], Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackPage));
        handler.Push(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Push(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("collection.write permission denied")
        });
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), maxConcurrency: 1, deleteUnavailable: true);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => library.GetFavorites());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("DELETE", ex.Message);
    }

    [Fact]
    public async Task Empty_tidal_collection_reports_zero_metadata_processed()
    {
        var handler = new ScriptedHandler();
        handler.Push(Response(new TidalCollectionPage(), AppJsonContext.Default.TidalCollectionPage));
        var updates = new List<SyncProgress>();
        var library = new TidalLibrary(new HttpClient(handler), () => Task.FromResult("test-access"),
            new Uri("https://tidal.test/"), progress: updates.Add);
        Assert.Empty(await library.GetFavorites());
        Assert.Contains(updates, p => p.Stage == "TIDAL metadata" &&
            p.Completed == 0 && p.Total == 0);
    }

}
