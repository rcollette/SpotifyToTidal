# Spotify ↔ TIDAL Favorites Sync

A command-line tool that synchronizes **Spotify Liked Songs** and **TIDAL Favorite Tracks** in either direction. It only **adds** favorites: it never removes tracks from either service. You can preview changes before syncing.

Available for Windows, macOS (Intel and Apple Silicon), and Linux.

## Download and install

1. Open the [latest GitHub release](https://github.com/rcollette/SpotifyToTidal/releases).
2. Download the file for your computer:

   | Computer | Download filename ends with |
   | --- | --- |
   | Windows 64-bit | `win-x64.zip` |
   | macOS Apple Silicon (M1/M2/M3/M4 and later) | `osx-arm64.tar.gz` |
   | macOS Intel | `osx-x64.tar.gz` |
   | Linux 64-bit (x86-64) | `linux-x64.tar.gz` |

3. Extract the archive into a folder of your choice. Open a terminal (or PowerShell on Windows) **in that folder**.

These releases include a native, self-contained executable. **You do not need to install .NET.** macOS binaries are not currently signed/notarized, and Windows binaries are not code-signed, so operating-system security warnings may appear.

## Get your Spotify Client ID

You must register your own Spotify application; your normal Spotify account password is **not** a Client ID.

1. Sign in to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
2. Select **Create app** (or open an app you already manage). Follow the prompts to enter the app name and description; select the **Web API** if asked which API you will use.
3. Add this **Redirect URI** to the app's settings, and save it:

   ```text
   http://127.0.0.1:8765/callback/
   ```

4. Open the app's **Settings** and copy its **Client ID**. Use that value for `spotifyClientId`.

Spotify's development-mode apps require the app owner to have an active **Spotify Premium** subscription. Spotify also limits which accounts can authorize a development-mode app; add additional users in your developer dashboard if needed. See [Spotify's getting-started guide](https://developer.spotify.com/documentation/web-api/tutorials/getting-started) and [redirect URI requirements](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri).

## Get your TIDAL Client ID

1. Sign in to the [TIDAL Developer Portal](https://developer.tidal.com/).
2. Go to **Manage apps**, and register an application (or open one you already registered).
3. Configure this exact OAuth **Redirect URI**:

   ```text
   http://127.0.0.1:8766/callback/
   ```

4. Copy the application's **Client ID** into `tidalClientId`. The application needs permission to read and modify the user's collection.

TIDAL's developer portal controls application registration and permissions. If you cannot create an app or the requested collection permissions are unavailable, refer to [TIDAL's authorization documentation](https://developer.tidal.com/documentation/api-sdk/api-sdk-authorization) or its developer support resources. This tool uses **OAuth authorization-code flow with PKCE** and needs **Client IDs, not Client Secrets**.

## Configure the application

The extracted download contains `appsettings.example.json`. Copy it to a `config.json` file in the `.spotify-to-tidal` directory of your home folder.

**macOS / Linux:**

```sh
mkdir -p ~/.spotify-to-tidal
cp appsettings.example.json ~/.spotify-to-tidal/config.json
```

**Windows (PowerShell):**

```powershell
New-Item -ItemType Directory -Force "$HOME\.spotify-to-tidal"
Copy-Item .\appsettings.example.json "$HOME\.spotify-to-tidal\config.json"
```

Edit the resulting `config.json` and replace the two `REPLACE_...` placeholders. The complete default configuration is:

```json
{
  "spotifyClientId": "YOUR_SPOTIFY_CLIENT_ID",
  "tidalClientId": "YOUR_TIDAL_CLIENT_ID",
  "spotifyRedirectUri": "http://127.0.0.1:8765/callback/",
  "tidalRedirectUri": "http://127.0.0.1:8766/callback/",
  "countryCode": "US",
  "tidalScopes": "collection.read collection.write"
}
```

The redirect URIs must **exactly match** those registered in the corresponding developer portals, including the final slash. Use `127.0.0.1`, not `localhost`. `countryCode` is the two-letter country code used for TIDAL catalog requests; change `US` if applicable. Your TIDAL app must be permitted to request the scopes configured in `tidalScopes`.

## Sign in and sync

Run these commands in the extracted application folder.

**macOS / Linux:**

```sh
./SpotifyToTidal login spotify
./SpotifyToTidal login tidal
./SpotifyToTidal sync --dry-run
./SpotifyToTidal sync
```

**Windows (PowerShell):**

```powershell
.\SpotifyToTidal.exe login spotify
.\SpotifyToTidal.exe login tidal
.\SpotifyToTidal.exe sync --dry-run
.\SpotifyToTidal.exe sync
```

The login commands open a browser to authorize each account. If the browser does not open, copy the authorization URL printed in the terminal and visit it manually. Return to the terminal after approving access.

Run `sync --dry-run` first to preview additions without changing either library. Run `sync` to add matching favorites in **both** directions. You can also choose one direction:

| Command arguments | Action |
| --- | --- |
| `sync --direction spotify-to-tidal` | Add Spotify Liked Songs to TIDAL Favorites |
| `sync --direction tidal-to-spotify` | Add TIDAL Favorites to Spotify Liked Songs |
| `sync --direction both` | Sync both ways (the default) |
| `sync --dry-run` | Preview changes; do not add anything |
| `report` | Print the location of the last CSV report |

The CSV report is written to `~/.spotify-to-tidal/report.csv` on macOS/Linux or `%USERPROFILE%\.spotify-to-tidal\report.csv` on Windows. It includes outcomes such as `added`, `would-add`, `already-present`, `unmatched`, and `error`.

Tracks are matched conservatively using their **ISRC, title, and primary artist**. Tracks without one unambiguous match are reported rather than added. The application does not remove favorites. Spotify or TIDAL API permissions and catalog availability can limit which songs can be transferred.

OAuth tokens are saved in the same configuration directory as JSON files. Keep that directory private; do not share your tokens or `config.json`.

This project is not affiliated with Spotify or TIDAL.

**Interested in developing or building from source?** See [CONTRIBUTING.md](https://github.com/rcollette/SpotifyToTidal/blob/main/CONTRIBUTING.md).
