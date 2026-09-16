# Remote Control API v1

OpenTaiko can expose an HTTP API and quick song-selection page on the local computer or local network. The feature is disabled by default.

## Configuration

Add these sections to `Config.ini` or start and exit OpenTaiko once to write the defaults:

```ini
[Startup]
SkipTitleScreen=0
PlayerMode=Prompt
DefaultSaveSlot=1
DefaultPlayerSide=Left

[Online]
EnableNetworkConnectivityCheck=1
EnableDiscordRpc=1

[RemoteControl]
Enabled=0
Host=127.0.0.1
Port=2354
ServeWebUI=1
OpenWebUIOnStartup=0

[PlayHistory]
Enabled=1
MaxEntries=100
```

`PlayerMode` accepts `Prompt`, `1P`, or `2P`. `1P` and `2P` skip the title screen's "Press P" player-entry step and synchronize `PlayerCount`, while `Prompt` preserves the original interaction. `DefaultSaveSlot` accepts 1 or 2. `DefaultPlayerSide` accepts `Left` or `Right` and only affects single-player startup.

`RemoteControl.Host` accepts `127.0.0.1` (same computer only) or `0.0.0.0` (all network interfaces). LAN mode has no API authentication or pairing; every device that can reach the port can inspect the song library and control the game. Use it only on a trusted private network and allow TCP port 2354 through Windows Firewall when prompted.

When enabled, open `http://127.0.0.1:2354/` on the game computer. In LAN mode, other devices use `http://<game-computer-ip>:2354/`. The JSON API is under `/api/v1` on the same address.

## Endpoints

| Method | Path | Description |
| --- | --- | --- |
| GET | `/health` | Server version and song-index readiness |
| GET | `/state` | Current stage, selection, difficulty, and player count |
| GET | `/songs` | Search and paginate songs |
| GET | `/songs/{songId}` | Fetch one song |
| GET | `/songs/{songId}/audio` | Stream the song audio for in-browser preview |
| GET | `/genres` | List available song categories |
| GET | `/playlists` | Read all independently managed web playlists |
| GET | `/playlists/export` | Export all playlists as versioned JSON |
| GET | `/history?limit=100` | Recent play events, newest first |
| GET | `/commands/{commandId}` | Read asynchronous command status |
| GET | `/events` | Server-sent state, history, command, and gameplay events |
| POST | `/selection` | Select a song and difficulty |
| POST | `/play` | Select and start a chart |
| POST | `/preview` | Select a song and start its normal preview flow |
| POST | `/preview/stop` | Stop preview audio |
| POST | `/favorite` | Set a song's favorite state for the active save slot |
| POST | `/playlists` | Create a playlist |
| PATCH | `/playlists/{playlistId}` | Rename a playlist |
| POST | `/playlists/{playlistId}/songs` | Add a song to a playlist |
| PUT | `/playlists/{playlistId}/songs` | Replace the song order |
| PUT | `/playlists/import` | Import and replace all web playlists |
| POST | `/restart` | Replay the latest or specified history entry |
| POST | `/gameplay/exit` | Safely stop the current play and return to song selection |
| POST | `/gameplay/retry` | Restart the current chart without returning to song selection |
| POST | `/history/{historyId}/play` | Replay a specified history entry |
| DELETE | `/playlists/{playlistId}` | Delete a playlist, including the last one |
| DELETE | `/playlists/{playlistId}/songs/{songId}` | Remove a song from a playlist |

`GET /songs` supports `query`, `genre`, `difficulty`, `minLevel`, `maxLevel`, `favorite`, `page`, and `pageSize`. Page size defaults to 100 and is limited to 500. Difficulty values are `easy`, `normal`, `hard`, `oni`, `ura`, `tower`, and `dan`.

The quick-selection page separates the catalog, custom playlists, and recent plays into tabs. The catalog loads additional pages automatically as the user scrolls and refreshes genres, songs, playlists, and history when the game finishes building its song index. Blank source genres are exposed as `OTHER`. Long titles, makers, playlist names, and unbroken strings are clamped so they cannot widen the page; the full song text remains available through the browser title tooltip. Web playlists are independent of OpenTaiko's Favorite and Recent folders, support create/rename/delete, multi-list membership, drag ordering, and JSON import/export, and are stored in `RemotePlaylists.json`. Existing `RemotePlaylist.json` data is migrated automatically. Recent plays show category and every matching playlist badge, favorite state, and both preview actions. "Web preview" plays audio only in the browser. "Game Preview" sends `/preview`, moves the in-game song selection immediately, and uses OpenTaiko's normal preview playback. Favorites remain stored in the game's existing `Favorite.json`.

POST bodies use UTF-8 `application/json`. Accepted commands return HTTP 202 with a `commandId`; poll the command endpoint or subscribe to SSE for completion. Commands return `GAME_BUSY` when they are unavailable in the current stage.

`GET /state` and SSE `state` events include `playbackStatus`, song metadata, elapsed and total milliseconds, progress from 0 to 1, and `canExit`, `canRetry`, and `canSelectSong` capability flags. Gameplay progress is throttled to approximately one update per second. Selection, play, and history replay commands are also accepted on the results screen; OpenTaiko completes result/history persistence, returns to standard song selection, and then applies the requested song. Gameplay exit and retry remain restricted to safe gameplay phases.

## Troubleshooting

- If the page does not open, confirm `RemoteControl.Enabled=1` and that the configured port is unused.
- For LAN access, set `Host=0.0.0.0`, keep the game running, use the game computer's private IPv4 address, and allow the configured TCP port through Windows Firewall.
- LAN mode intentionally has no authentication. Do not expose the port to the public internet or configure router port forwarding.
- If `PlayHistory.json` is damaged, OpenTaiko preserves it as `PlayHistory.json.corrupt-{timestamp}` and starts an empty history.
- The legacy game-event stream remains available at `/` when the request accepts `text/event-stream`; new clients should use `/api/v1/events`.
