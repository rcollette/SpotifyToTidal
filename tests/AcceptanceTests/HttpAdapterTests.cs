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
            new Uri("https://tidal.test/"), "US", wait);

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
        h.Push(Response(new TidalTrackDetail {
            Data = TidalTrack("tid-1"), Included = [TidalArtist()]
        }, AppJsonContext.Default.TidalTrackDetail));
        h.Push(Response(new TidalCollectionPage {
            Data = [new TidalResourceDto { Id = "tid-1" }, new TidalResourceDto { Id = "tid-2" }],
            Links = new TidalLinksDto { Next = "/v2/favorites?page=2" }
        }, AppJsonContext.Default.TidalCollectionPage));
        h.Push(Response(new TidalTrackDetail {
            Data = TidalTrack("tid-1"), Included = []
        }, AppJsonContext.Default.TidalTrackDetail));
        h.Push(Response(new TidalTrackDetail {
            Data = TidalTrack("tid-2", relationship:false, artistFallback:"Fallback Artist"), Included = []
        }, AppJsonContext.Default.TidalTrackDetail));
        var list = await Tidal(h).GetFavorites();
        Assert.Equal(2, list.Count);
        Assert.Equal("Singer", list[0].Artist);
        Assert.Equal("Fallback Artist", list[1].Artist);
        Assert.Equal(5, h.Requests.Count);
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
        Assert.Empty(await lib.GetFavorites());
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(waits));
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
        Assert.True(ex.Message.Length < 440);
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
        Assert.Empty(await Tidal(h).GetFavorites());
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
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("GET https://tidal.test/v2/tracks/not-available", exception.Message);
        Assert.DoesNotContain("ask TIDAL developer support", exception.Message);
    }


}
