# Contributing to SpotifyToTidal

This document is for developers who want to clone, configure, test, modify, or build the application. For installing an existing release, registering Spotify/TIDAL apps, and using the CLI, see the [user guide (README.md)](README.md).

## Development prerequisites

- **.NET 10 SDK**: install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The repository's `global.json` selects .NET 10 and permits newer .NET 10 feature-band SDKs.
- **Git** and a C# editor or IDE (optional).
- For **Native AOT publishing**, install the toolchain for your build machine:
  - **Windows**: Visual Studio 2022 or newer with the **Desktop development with C++** workload.
  - **macOS**: Xcode Command Line Tools (`xcode-select --install`).
  - **Ubuntu/Debian Linux**: `clang` and `zlib1g-dev` (`sudo apt-get install clang zlib1g-dev`).
  - For other platforms, consult the [.NET Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

Native AOT publishing is **platform-specific**. To build the native macOS executable, use a macOS build host; to build Windows, use Windows; and to build Linux, use Linux. The release workflow performs these builds on the appropriate hosted runners.

## Clone and set up

```sh
git clone https://github.com/rcollette/SpotifyToTidal.git
cd SpotifyToTidal
dotnet --version
dotnet restore SpotifyToTidal.csproj
dotnet build SpotifyToTidal.csproj -c Debug
```

`SpotifyToTidal.csproj` targets `net10.0` and sets `PublishAot=true` for native release builds. You can perform ordinary development builds without first producing a native executable.

## Development configuration and API credentials

The application loads `config.json` and its persisted OAuth tokens from the
**user's home directory**, not the source or build output directory.

| OS | Configuration file |
| --- | --- |
| macOS / Linux | `~/.spotify-to-tidal/config.json` |
| Windows | `%USERPROFILE%\.spotify-to-tidal\config.json` |

### Register developer applications

To test against real music accounts, obtain an application **Client ID** from
*each* provider. Both providers use authorization-code PKCE in this CLI; do not
check a Client Secret into the repository.

**Spotify**

1. Open the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
   and create/select an application with **Web API** access.
2. Set its redirect URL to `http://127.0.0.1:8765/callback/` in its app settings.
3. Copy the application's Client ID to `spotifyClientId`.
4. Verify your developer app's Premium requirement and account allowlist.
   See [Spotify app setup](https://developer.spotify.com/documentation/web-api/concepts/apps)
   and [development-mode rules](https://developer.spotify.com/documentation/web-api/concepts/quota-modes).

**TIDAL**

1. Sign in to the [TIDAL Developer Portal](https://developer.tidal.com/)
   and open **Manage apps**.
2. Create/select an app, register `http://127.0.0.1:8766/callback/` as the redirect,
   and copy its Client ID to `tidalClientId`.
3. Ensure the app is permitted to request collection read/write scopes.
   TIDAL controls access to API endpoints and OAuth scopes. See
   [TIDAL authorization](https://developer.tidal.com/documentation/api-sdk/api-sdk-authorization).

The callback paths require the **final slash** and the literal loopback IP address
`127.0.0.1`. The defaults are hard-coded in `appsettings.example.json` and must
match the settings registered with the providers.

### Create local configuration

Run from the repository's root directory.

**macOS / Linux**

```sh
mkdir -p ~/.spotify-to-tidal
cp appsettings.example.json ~/.spotify-to-tidal/config.json
```

**Windows PowerShell**

```powershell
New-Item -ItemType Directory -Force "$HOME\.spotify-to-tidal"
Copy-Item .\appsettings.example.json "$HOME\.spotify-to-tidal\config.json"
```

Update the copied `config.json`:

```json
{
  "spotifyClientId": "REPLACE_SPOTIFY_CLIENT_ID",
  "tidalClientId": "REPLACE_TIDAL_CLIENT_ID",
  "spotifyRedirectUri": "http://127.0.0.1:8765/callback/",
  "tidalRedirectUri": "http://127.0.0.1:8766/callback/",
  "countryCode": "US",
  "tidalScopes": "collection.read collection.write"
}
```

Replace both `REPLACE_...` values. Change `countryCode` if your TIDAL market is
not the US; only request scopes your TIDAL app is allowed to use.

Spotify OAuth scopes (`user-library-read user-library-modify`) are specified by
`OAuthManager` in `Program.cs`; TIDAL OAuth scopes come from `tidalScopes`.
Login tokens are saved alongside `config.json` as `spotify-token.json` and
`tidal-token.json`. **Do not commit or share credentials or tokens.**

Local unit and HTTP acceptance tests require **no Client IDs, user accounts,
or live provider access**. Use developer credentials only when validating real
provider integrations.

## Run and debug

```sh
dotnet run --project SpotifyToTidal.csproj -- login spotify
dotnet run --project SpotifyToTidal.csproj -- login tidal
dotnet run --project SpotifyToTidal.csproj -- sync --dry-run
dotnet run --project SpotifyToTidal.csproj -- sync
dotnet run --project SpotifyToTidal.csproj -- report
```

The CLI opens the authorization URL in the browser and listens for the `http://127.0.0.1:.../callback/` response. The output CSV is written to the configuration directory as `report.csv`. If you are investigating an API issue, use `--dry-run` before running write operations.

## Run automated tests

The repository has separate **xUnit** test projects. Docker, live music accounts, and application credentials are not needed for these suites.

```sh
dotnet test tests/UnitTests/UnitTests.csproj -c Release -p:TreatWarningsAsErrors=true
dotnet test tests/AcceptanceTests/AcceptanceTests.csproj -c Release -p:TreatWarningsAsErrors=true
```

The acceptance suite uses deterministic HTTP handlers to exercise the production Spotify/TIDAL adapters, OAuth PKCE, CLI, error handling, and sync behavior. It does not prove that the current live Spotify/TIDAL API grants access to all endpoints.

**Coverage policy:** GitHub Actions merges Coverlet results from **both** test projects and requires **100% line, branch, and method coverage** for hand-authored application code. Only compiler-generated serializer files are excluded. This is an aggregate coverage requirement; each project does not independently have to exercise every code path.

The authoritative coverage commands and reports are in [`.github/workflows/ci.yml`](.github/workflows/ci.yml), in the `coverage` job. Do not reduce coverage thresholds or exclude hand-written application logic to make checks pass.

## Build and publish

To compile the application without producing a native distribution:

```sh
dotnet build SpotifyToTidal.csproj -c Release -p:TreatWarningsAsErrors=true
```

To create a **self-contained Native AOT** application on a machine with the matching native toolchain, use the corresponding command:

```sh
# Run on a Linux x64 host
dotnet publish SpotifyToTidal.csproj -c Release -r linux-x64 --self-contained true -p:TreatWarningsAsErrors=true

# Run on an Apple Silicon macOS host
dotnet publish SpotifyToTidal.csproj -c Release -r osx-arm64 --self-contained true -p:TreatWarningsAsErrors=true

# Run on an Intel macOS host
dotnet publish SpotifyToTidal.csproj -c Release -r osx-x64 --self-contained true -p:TreatWarningsAsErrors=true

# Run on a Windows x64 host
dotnet publish SpotifyToTidal.csproj -c Release -r win-x64 --self-contained true -p:TreatWarningsAsErrors=true
```

Use **only** the command corresponding to the OS and architecture of the machine performing the compilation. The executable is under `bin/Release/net10.0/<RID>/publish/`. For a non-AOT build while debugging a native-compilation issue, pass `-p:PublishAot=false`.

### JSON and Native AOT compatibility

All production JSON request/response objects must use **concrete DTO types** with source-generated `System.Text.Json` metadata from `AppJsonContext` (`ApiJsonModels.cs`). Use the `JsonSerializer` overloads that accept the relevant generated `JsonTypeInfo<T>` rather than reflection-based or loosely typed serialization. Native AOT, trimming, and compiler warnings are checked in CI.

The synchronization algorithm deliberately matches a destination only when ISRC, normalized title, and primary artist identify exactly one candidate. Never weaken matching or introduce removal of existing favorites without explicitly changing the stated product behavior and tests.

## Pull requests and CI

Create a topic branch, make your changes, and open a pull request against `main`. The [CI workflow](.github/workflows/ci.yml) runs on pull requests and includes:

- Unit and acceptance tests on **Linux x64, Windows x64, macOS Intel, and macOS Apple Silicon**.
- Native AOT publish and executable smoke tests on the same four targets.
- One combined Coverlet coverage gate at **100% line / branch / method**.
- Warning-as-error builds; the repository's ruleset may also require CodeQL scanning.

Check that all relevant jobs finish successfully before merging.

### Releasing

The [Release workflow](.github/workflows/release.yml) packages the application for the same four targets. On pull requests, it verifies packaging but does **not** publish.

To publish a version, use **Actions → Release → Run workflow** on `main`, enter a new version such as `v0.1.0`, and optionally mark it as a prerelease. The workflow checks and packages all targets before it creates the GitHub Release and attaches four archives: `.zip` for Windows, `.tar.gz` for Linux and both macOS architectures. A version tag must not already exist.

Mac binaries are currently unsigned/not notarized and Windows binaries are not code-signed. Signing and notarization would need to be added separately before distributing trusted installers.

## Official references

- [Spotify Web API documentation](https://developer.spotify.com/documentation/web-api)
- [TIDAL API authorization](https://developer.tidal.com/documentation/api-sdk/api-sdk-authorization)
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
