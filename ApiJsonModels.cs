using System.Text.Json.Serialization;

namespace SpotifyToTidal;

// Explicit wire contracts: all JSON used by production code has statically generated metadata.
public sealed class SpotifySavedTracksPage
{
    [JsonPropertyName("items")] public SpotifySavedItem[] Items { get; init; } = [];
    [JsonPropertyName("next")] public string? Next { get; init; }
}

public sealed class SpotifySavedItem
{
    [JsonPropertyName("track")] public SpotifyTrackDto? Track { get; init; }
}

public sealed class SpotifySearchResponse
{
    [JsonPropertyName("tracks")] public SpotifyTrackPage? Tracks { get; init; }
}

public sealed class SpotifyTrackPage
{
    [JsonPropertyName("items")] public SpotifyTrackDto[] Items { get; init; } = [];
}

public sealed class SpotifyTrackDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("artists")] public SpotifyArtistDto[] Artists { get; init; } = [];
    [JsonPropertyName("external_ids")] public SpotifyExternalIdsDto? ExternalIds { get; init; }
    [JsonPropertyName("is_local")] public bool IsLocal { get; init; }
}

public sealed class SpotifyArtistDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed class SpotifyExternalIdsDto
{
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
}

public sealed class TidalResourceDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed class TidalLinksDto
{
    [JsonPropertyName("next")] public string? Next { get; init; }
}

public sealed class TidalCollectionPage
{
    [JsonPropertyName("data")] public TidalResourceDto[] Data { get; init; } = [];
    [JsonPropertyName("links")] public TidalLinksDto? Links { get; init; }
}

public sealed class TidalTrackPage
{
    [JsonPropertyName("data")] public TidalTrackDto[] Data { get; init; } = [];
    [JsonPropertyName("included")] public TidalArtistDto[] Included { get; init; } = [];
    [JsonPropertyName("links")] public TidalLinksDto? Links { get; init; }
}

public sealed class TidalTrackDetail
{
    [JsonPropertyName("data")] public TidalTrackDto? Data { get; init; }
    [JsonPropertyName("included")] public TidalArtistDto[] Included { get; init; } = [];
}

public sealed class TidalTrackDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("attributes")] public TidalTrackAttributesDto? Attributes { get; init; }
    [JsonPropertyName("relationships")] public TidalRelationshipsDto? Relationships { get; init; }
}

public sealed class TidalTrackAttributesDto
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
}

public sealed class TidalRelationshipsDto
{
    [JsonPropertyName("artists")] public TidalArtistRelationshipDto? Artists { get; init; }
}

public sealed class TidalArtistRelationshipDto
{
    [JsonPropertyName("data")] public TidalResourceDto[] Data { get; init; } = [];
}

public sealed class TidalArtistDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("attributes")] public TidalArtistAttributesDto? Attributes { get; init; }
}

public sealed class TidalArtistAttributesDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed class TidalCollectionWriteRequest
{
    [JsonPropertyName("data")] public TidalResourceDto[] Data { get; init; } = [];
}

public sealed class EmptyApiResponse { }

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; } = 3600;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(OAuthToken))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(SpotifySavedTracksPage))]
[JsonSerializable(typeof(SpotifySearchResponse))]
[JsonSerializable(typeof(SpotifyTrackPage))]
[JsonSerializable(typeof(TidalCollectionPage))]
[JsonSerializable(typeof(TidalTrackPage))]
[JsonSerializable(typeof(TidalTrackDetail))]
[JsonSerializable(typeof(TidalCollectionWriteRequest))]
[JsonSerializable(typeof(EmptyApiResponse))]
public partial class AppJsonContext : JsonSerializerContext;
