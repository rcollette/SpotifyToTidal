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

1. Copy \`appsettings.example.json\` to \`~/.spotify-to-tidal/config.json\`
   (Windows: \`%USERPROFILE%\.spotify-to-tidal\config.json\`).
2. Fill in the public OAuth application client IDs and exact registered loopback redirect URLs.
3. Execute:

\`\`\`sh
dotnet run -- login spotify
dotnet run -- login tidal
dotnet run -- sync --dry-run
dotnet run -- sync
dotnet run -- sync --direction spotify-to-tidal
dotnet run -- sync --direction tidal-to-spotify
dotnet run -- report
\`\`\`

Sync **only adds** favorites. It does not delete/unlike songs on either service.
Preview writes nothing; the CSV report lists statuses including \`would-add\`,
\`added\`, \`already-present\`, \`unmatched\` and \`error\`. Re-running a partial
sync is safe because both libraries are fetched before computing additions.

OAuth access/refresh token JSON files are stored under the user's home directory.
Restrict access to that directory. Never commit tokens or \`config.json\`.

## Unit and acceptance tests

Two **independent GitHub Actions jobs** run on every pull request and pushes to \`main\`.
No GitHub secrets, real Spotify/TIDAL accounts, or Docker are required.

| Job | Command | Coverage |
| --- | --- | --- |
| \`unit-tests\` | \`dotnet run --project tests/UnitTests/UnitTests.csproj -c Release\` | Normalization, exact matching, mismatches, missing ISRCs, ambiguous results, CSV escaping |
| \`acceptance-tests\` | \`dotnet run --project tests/AcceptanceTests/AcceptanceTests.csproj -c Release\` | Simulated Spotify and TIDAL JSON/HTTP API, OAuth bearer headers, dry-run, both-way writes, one-way scope, idempotence, never-delete behavior, ambiguity handling |

The acceptance suite injects a fake \`HttpMessageHandler\` into the **actual**
\`SpotifyLibrary\` and \`TidalLibrary\` adapters. It exercises the real API
request paths, verbs, response deserialization and synchronization engine.
Mocked endpoints return deterministic catalogs; they do not require live web
access. Docker would add complexity without improving coverage for this
credential-free test layer.

All test projects intentionally use the .NET SDK alone (no external NuGet
test dependencies). A test case throws when it fails, and each suite returns
exit code 1 on failure, causing GitHub Actions to mark the job red.

## PR verification

Open a pull request targeting \`main\`; **CI** starts two distinct required-to-pass
jobs: \`unit-tests\` and \`acceptance-tests\`. Review both check results under
the pull request **Checks** tab. If desired, use GitHub branch protection/rulesets
to require the two checks before merging.

## Publish

\`\`\`sh
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r osx-x64 --self-contained true
dotnet publish -c Release -r osx-arm64 --self-contained true
\`\`\`

Native AOT compilation requires platform-specific tools, and macOS AOT builds
must run on macOS. For a standard self-contained build without AOT add
\`-p:PublishAot=false\`.

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
