# Spotify ↔ TIDAL Favorites Sync

A cross-platform **.NET 10** command-line application that additively synchronizes
**Spotify Liked Songs** and **TIDAL Favorite Tracks**. Supports both directions, one-way
sync and a preview mode. Songs without a unique exact ISRC + title + artist match
are reported, never automatically favorited.

> **Development status:** Automated tests use simulated, authenticated HTTP API responses
> and do **not** demonstrate that the live Spotify and TIDAL developer integrations have
> been granted all required scopes. Validate against both accounts before using write mode.

## Setup

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Register Spotify and TIDAL developer applications with compatible OAuth PKCE redirects.
Spotify development-mode apps require an eligible Premium account.

1. Copy `appsettings.example.json` to `~/.spotify-to-tidal/config.json`
   (Windows: `%USERPROFILE%\.spotify-to-tidal\config.json`).
2. Fill in the public OAuth application client IDs and exact registered loopback redirect URLs.
3. Execute:

```sh
dotnet run -- login spotify
dotnet run -- login tidal
dotnet run -- sync --dry-run
dotnet run -- sync
dotnet run -- sync --direction spotify-to-tidal
dotnet run -- sync --direction tidal-to-spotify
dotnet run -- report
```

Sync **only adds** favorites. It does not delete/unlike songs on either service.
Preview writes nothing; the CSV report lists statuses including `would-add`,
`added`, `already-present`, `unmatched` and `error`. Re-running a partial
sync is safe because both libraries are fetched before computing additions.

OAuth access/refresh token JSON files are stored under the user's home directory.
Restrict access to that directory. Never commit tokens or `config.json`.

## Testing and CI

On every pull request and push to `main`, CI runs a Linux/macOS/Windows test and
Native AOT matrix, plus stable aggregate checks. These checks use .NET 10 and require no real Spotify or TIDAL accounts,
developer credentials, GitHub secrets or Docker.

| Job | What it verifies |
| --- | --- |
| `unit-tests` | xUnit tests for recording matching, CSV escaping, error handling, cancellation, duplicates, and additive sync |
| `acceptance-tests` | xUnit tests exercising the real HTTP adapters, CLI, OAuth PKCE/refresh, pagination, throttling, one-way and bidirectional workflows against deterministic fake HTTP services |
| `coverage` | Coverlet merges unit **and** acceptance coverage and fails unless **line, branch and method coverage are each 100%** across all hand-authored application source |
| `native-aot` | Publishes and smoke-tests Native AOT executables across Linux, Windows, and both macOS architectures, with `TreatWarningsAsErrors=true` |

Run the xUnit suites locally with:

```sh
dotnet test tests/UnitTests/UnitTests.csproj -c Release
dotnet test tests/AcceptanceTests/AcceptanceTests.csproj -c Release
```

The acceptance suite injects an `HttpMessageHandler` into the actual
Spotify and TIDAL adapters. All HTTP responses and requests in application code and
test fixtures use concrete DTO types and `System.Text.Json` source-generated
metadata from `AppJsonContext`. No reflection-based JSON serialization is used.
The compiler-generated `AppJsonContext` implementation is excluded from coverage
because its branches are produced by Microsoft's generator, not by this project.
All hand-written application logic remains within the 100% coverage threshold.

### Pull request requirements

The repository's branch ruleset may also require a completed CodeQL analysis.
CodeQL is configured in the repository's **Settings → Advanced Security →
Code scanning** and must report results for the pull request's current head
commit before GitHub will permit merging.


Under GitHub's repository ruleset, require the exact status names:
`unit-tests`, `acceptance-tests`, `coverage`, and `native-aot`.
The ruleset's required-status-check list must actually contain those names;
simply running the checks is not sufficient to prevent merging failed checks.
Review results on each pull request's **Checks** tab.

## GitHub releases

Use **Actions → Release → Run workflow** after this PR has been merged to
`main`. Select the `main` branch, provide a new semantic-version tag such as
`v0.1.0`, and optionally mark the release as a prerelease.

The manually triggered workflow publishes four native-AOT release downloads:

| Release asset | Platform |
| --- | --- |
| `SpotifyToTidal-v0.1.0-linux-x64.tar.gz` | Linux x86-64 |
| `SpotifyToTidal-v0.1.0-osx-arm64.tar.gz` | macOS Apple Silicon |
| `SpotifyToTidal-v0.1.0-osx-x64.tar.gz` | macOS Intel |
| `SpotifyToTidal-v0.1.0-win-x64.zip` | Windows x86-64 |

Each archive contains a native self-contained executable, configuration example, and
README. Unix releases are `.tar.gz` files so extraction preserves executable permissions;
Windows uses `.zip`. All archives are built on their target OS and smoke-tested before GitHub
creates the version tag and attaches the archives to the public **Releases** page.
No release is published on pull requests: those runs only verify the packaging and
provide temporary CI artifacts. Version tags are immutable in this workflow—use a
new version for each subsequent release. Both macOS archives are unsigned and
not notarized; Windows binaries are unsigned. Operating-system security prompts
may appear until signing is set up.

## Publish

```sh
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r osx-x64 --self-contained true
dotnet publish -c Release -r osx-arm64 --self-contained true
```

Native AOT compilation requires platform-specific tools, and macOS AOT builds
must run on macOS. For a standard self-contained build without AOT add
`-p:PublishAot=false`.

## Known limitations / API validation

- Live TIDAL collection-write permissions, authorization scopes and response
  payloads remain unverified until the developer applications are registered.
- A track may exist in multiple versions under one ISRC; the matcher deliberately
  declines ambiguous or incomplete matches.
- The search adapters use available catalog metadata and pagination. When the
  TIDAL artist relationship isn't returned, the track is skipped rather than
  risking an incorrect favorite.
- This project is not affiliated with Spotify or TIDAL.

Official documentation:
- https://developer.spotify.com/documentation/web-api/reference/get-users-saved-tracks
- https://developer.spotify.com/documentation/web-api/reference/save-library-items
- https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide
- https://tidal-music.github.io/tidal-api-reference/
