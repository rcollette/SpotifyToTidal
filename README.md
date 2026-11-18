# SpotifyToTidal

Synchronize **Spotify Liked Songs** and **TIDAL Favorite Tracks** in either direction, or both ways. SpotifyToTidal **only adds** favorites by default. An explicit optional cleanup flag can remove TIDAL favorites whose track metadata is unavailable. Use a dry run to preview changes before syncing.

Runs on Windows, macOS (Apple Silicon or Intel), and Linux as a command-line application.

## 1. Download and install

Download the appropriate archive from **[GitHub Releases](https://github.com/rcollette/SpotifyToTidal/releases)**.

| Computer | Release filename suffix |
| --- | --- |
| Windows x64 | `win-x64.zip` |
| macOS Apple Silicon | `osx-arm64.tar.gz` |
| macOS Intel | `osx-x64.tar.gz` |
| Linux x64 | `linux-x64.tar.gz` |

Extract the archive into a folder and open Terminal or PowerShell **in that folder**. Releases contain a native, self-contained executable: **you do not need to install .NET**.

> **Security notice:** macOS releases are currently unsigned and not notarized; Windows releases are not code-signed. Your operating system may show a warning.

## 2. Get your Spotify Client ID

1. Sign in to the **[Spotify Developer Dashboard](https://developer.spotify.com/dashboard)**.
2. Create an application (or open one you already own). Select **Web API** when asked which API you intend to use.
3. In the app's settings, add this **Redirect URI** and save:

   ```text
   http://127.0.0.1:8765/callback/
   ```

4. Copy the app's **Client ID** from its settings. You will put it in `spotifyClientId`.

This tool uses PKCE, so you **do not need the Client Secret**. Spotify requires Premium for the owner of a development-mode app and restricts which Spotify accounts may authorize it. If another account will sign in, add it to the app's user allowlist when applicable.

See Spotify's [app registration guide](https://developer.spotify.com/documentation/web-api/concepts/apps), [redirect URI rules](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri), and [development-mode restrictions](https://developer.spotify.com/documentation/web-api/concepts/quota-modes).

## 3. Get your TIDAL Client ID

1. Sign in to the **[TIDAL Developer Portal](https://developer.tidal.com/)**.
2. Open **Manage apps** and register an application, or select an existing one.
3. Set its OAuth **Redirect URI** to:

   ```text
   http://127.0.0.1:8766/callback/
   ```

4. Copy its **Client ID** into `tidalClientId`. The app must be permitted to request collection read/write access for synchronization.

A Client Secret is **not needed** for the PKCE authorization flow. If the portal does not grant the requested scopes, syncing the corresponding collection may not work. See [TIDAL's OAuth documentation](https://developer.tidal.com/documentation/api-sdk/api-sdk-authorization).

## 4. Configure

From the extracted application directory, copy `appsettings.example.json` to `config.json` in a `.spotify-to-tidal` folder in your home directory.

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

Edit `config.json` and replace the two Client ID placeholders. Keep the redirect addresses exactly as registered, **including the trailing slashes**:

```json
{
  "spotifyClientId": "YOUR_SPOTIFY_CLIENT_ID",
  "tidalClientId": "YOUR_TIDAL_CLIENT_ID",
  "spotifyRedirectUri": "http://127.0.0.1:8765/callback/",
  "tidalRedirectUri": "http://127.0.0.1:8766/callback/",
  "countryCode": "US",
  "tidalScopes": "collection.read collection.write",
  "spotifyRequestsPer30Seconds": null,
  "tidalRequestsPerMinute": null
}
```

Use `127.0.0.1`, not `localhost`. Change `countryCode` for your TIDAL market if necessary. Keep your `config.json` and the OAuth token files private.

## 5. Sign in and sync

Run these commands from the extracted application directory.

**macOS / Linux**

```sh
./SpotifyToTidal login spotify
./SpotifyToTidal login tidal
./SpotifyToTidal sync --dry-run
./SpotifyToTidal sync
```

**Windows PowerShell**

```powershell
.\SpotifyToTidal.exe login spotify
.\SpotifyToTidal.exe login tidal
.\SpotifyToTidal.exe sync --dry-run
.\SpotifyToTidal.exe sync
```

Each `login` opens a browser for authorization. If the browser does not open, visit the URL printed in the terminal and complete authorization there.

**Review the dry-run results before using `sync`.** By default, `sync` adds eligible favorites in both directions. Other options:

| Command arguments | Purpose |
| --- | --- |
| `sync --direction spotify-to-tidal` | Spotify → TIDAL only |
| `sync --direction tidal-to-spotify` | TIDAL → Spotify only |
| `sync --direction both` | Both directions (default) |
| `sync --dry-run` | Preview without adding favorites |
| `sync --max-concurrency N` | Set maximum simultaneous operations per service (1–32; default 5); this is **not** a requests-per-second budget |
| `sync --prune-unavailable-tidal` | Also remove TIDAL favorites whose individual track metadata cannot be retrieved; **destructive, opt-in** |
| `sync --dry-run --prune-unavailable-tidal` | Preview cleanup candidates without deleting them |
| `report` | Print the last CSV report's location |
| `--help` or `help` | Show command help, including sync flags; `sync --help` also works |

**Progress and performance:** The console reports how many Spotify and TIDAL favorites have been loaded, the number of TIDAL metadata lookups completed out of the collection total, and progress in each sync direction. The two directions run concurrently with bounded workers. Existing favorites are matched using in-memory indexes, duplicate ISRC catalog searches share a lookup within each direction, and TIDAL metadata is requested in batches of up to 20 tracks. Pages that depend on a cursor remain ordered.

**Cleanup examples (macOS/Linux):**

```sh
# Preview TIDAL favorites that would be deleted; do not change either library
./SpotifyToTidal sync --dry-run --prune-unavailable-tidal

# Delete confirmed unavailable TIDAL favorites (and perform normal sync)
./SpotifyToTidal sync --prune-unavailable-tidal
```

**TIDAL cleanup safety:** `--prune-unavailable-tidal` deletes only favorites confirmed by an individual metadata request to return HTTP 404 or to have no track metadata attributes. Missing or incomplete batch results are checked individually before any deletion is considered. It does **not** delete songs simply because they lack an ISRC or exact match. The application first reads the complete favorites collection before attempting removals; it refuses bulk cleanup when every track is unavailable (which could indicate missing API permissions). Run `sync --dry-run --prune-unavailable-tidal` first and review the console. Deletion requires TIDAL collection write access and cannot be undone by the application.

The report is saved as `~/.spotify-to-tidal/report.csv` (macOS/Linux) or `%USERPROFILE%\.spotify-to-tidal\report.csv` (Windows). It identifies added, existing, unmatched, and failed tracks.

Apart from explicitly requested TIDAL cleanup, the tool never removes existing favorites. Tracks are added only when the **ISRC, title, and primary artist** identify a unique match. Missing or ambiguous matches are reported, not added. Catalog access and API permissions can affect results.


## Rate limiting, HTTP 429 retries, and error logging

The application coordinates API requests **per vendor across concurrent workers**. A worker waits for a shared cooldown established by another worker's HTTP 429, instead of independently retrying while other workers continue to overload the service. Any locally configured sliding-window budget is also enforced before dispatch, regardless of `--max-concurrency`.

**Published vendor guidance:**

- [Spotify's rate-limit guide](https://developer.spotify.com/documentation/web-api/concepts/rate-limits) defines a **rolling 30-second window** and normally returns a **Retry-After (seconds)** header with HTTP 429. Spotify does **not** publish one numeric limit that applies to every application: the number varies with quota mode and possibly endpoint.
- [Spotify's development-mode quota guide](https://developer.spotify.com/documentation/web-api/concepts/quota-modes) describes a separate `429` error containing `"reason": "QUOTA_EXCEEDED"`. This is reported as an **error without retries**, because short exponential waits do not reset that quota.
- [TIDAL's API reference](https://tidal-music.github.io/tidal-api-reference/) does not specify a universal numeric request budget or one universally guaranteed 429 delay. TIDAL 429 responses are handled according to their `Retry-After` header when present; otherwise the fallback below applies.

**Configurable client-side limits** (set in `~/.spotify-to-tidal/config.json`, optional):

| JSON setting | Meaning | Default |
| --- | --- | --- |
| `spotifyRequestsPer30Seconds` | Maximum Spotify requests in any rolling 30 seconds | `null` (no assumed numeric vendor cap) |
| `tidalRequestsPerMinute` | Maximum TIDAL requests in any rolling 60 seconds | `null` (no assumed numeric vendor cap) |

Set a **positive integer** to apply a local budget. For example, `"spotifyRequestsPer30Seconds": 30` establishes a *user-chosen* limit of 30 Spotify API calls in a rolling 30-second interval. **30 is an example, not a vendor-published limit.** The default `null` still enforces shared reactive cooldowns upon 429s, but no numeric proactive cap can be guaranteed when the vendor does not publish a value. Local budgets cover requests made through the Spotify and TIDAL catalog/collection HTTP adapters; OAuth token requests use separate endpoints.

**Retry behavior:** Requests receiving HTTP 429 or 5xx get up to **five total attempts** (initial request plus four retries). A valid `Retry-After` header in either **delta-seconds or HTTP-date** form is honored in full, including waits beyond 60 seconds. Otherwise:

- HTTP 429 falls back to provider-wide exponential backoff starting at **2 seconds**, then **4, 8, 16, 32, 60 seconds** for successive 429s (60-second *fallback* maximum, **not** a cap on vendor instructions). The backoff counter **resets after any successful API response**.
- HTTP 5xx falls back to per-request exponential backoff of **2, 4, 8, 16 seconds** across the four retries, honoring `Retry-After` if supplied.
- Any 429 cooldown is shared among workers for that provider; if multiple 429 responses overlap, the later expiry wins. A configured local request budget remains active after success.

**Console diagnostics:** Every HTTP 429 produces a warning or terminal error showing the provider, HTTP status, method, path, attempt, selected retry strategy, effective wait time, and whether retries remain. All other unsuccessful HTTP responses (including 4xx and 5xx), transport failures, and invalid JSON responses are also logged to standard error; retried 5xx messages include backoff details. Logs omit request query parameters, OAuth tokens, and response bodies. A locally configured budget produces an informational waiting message when it delays a request. The command exits nonzero if an unhandled error prevents sync. A `--dry-run` still invokes the remote APIs and follows the same rules.

## Troubleshooting TIDAL 404 errors

If a sync reports **TIDAL returned 404**, first check the **HTTP method and URL** in the error. A 404 is not proof that the address is invalid: [TIDAL's API documentation](https://tidal-music.github.io/tidal-api-reference/) says that a resource unavailable to your application can appear not to exist.

- **`GET /v2/userCollectionTracks/me/relationships/items`**: Verify that your TIDAL developer app has permission for the `collection.read` scope, then run `SpotifyToTidal login tidal` again (or `./SpotifyToTidal login tidal` on macOS/Linux). If it still responds with 404, TIDAL may not have enabled track-collection access for your app; contact TIDAL developer support.
- **`GET /v2/tracks/<id>`**: The individual track may not be available or visible in your configured market. An isolated 404 is skipped with a warning; the app refuses to treat the whole collection as empty if every metadata lookup fails. Note the HTTP method and path when reporting this issue.

Do **not** share access tokens, refresh tokens, or authorization URLs from your terminal output.

---

**Want to build or contribute to the application?** See **[CONTRIBUTING.md](https://github.com/rcollette/SpotifyToTidal/blob/main/CONTRIBUTING.md)**.

This project is not affiliated with Spotify or TIDAL.
