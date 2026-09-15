# Remote Control API v1

OpenTaiko can expose a loopback-only HTTP API and quick song-selection page. The feature is disabled by default.

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

`PlayerMode` accepts `Prompt`, `1P`, or `2P`. `1P` and `2P` skip the title screen's "Press P" player-entry step and synchronize `PlayerCount`, while `Prompt` preserves the original interaction. `DefaultSaveSlot` accepts 1 or 2. `DefaultPlayerSide` accepts `Left` or `Right` and only affects single-player startup. Remote control v1 always binds to `127.0.0.1`; other host values fall back to loopback.

When enabled, open `http://127.0.0.1:2354/`. The JSON API base URL is `http://127.0.0.1:2354/api/v1`.

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
| POST | `/history/{historyId}/play` | Replay a specified history entry |
| DELETE | `/playlists/{playlistId}` | Delete a playlist, including the last one |
| DELETE | `/playlists/{playlistId}/songs/{songId}` | Remove a song from a playlist |

`GET /songs` supports `query`, `genre`, `difficulty`, `minLevel`, `maxLevel`, `favorite`, `page`, and `pageSize`. Page size defaults to 100 and is limited to 500. Difficulty values are `easy`, `normal`, `hard`, `oni`, `ura`, `tower`, and `dan`.

The quick-selection page separates the catalog, custom playlists, and recent plays into tabs. Web playlists are independent of OpenTaiko's Favorite and Recent folders, support create/rename/delete, multi-list membership, drag ordering, and JSON import/export, and are stored in `RemotePlaylists.json`. Existing `RemotePlaylist.json` data is migrated automatically. Recent plays show category and every matching playlist badge, favorite state, and both preview actions. "Web preview" plays audio only in the browser. "Game Preview" sends `/preview`, moves the in-game song selection immediately, and uses OpenTaiko's normal preview playback. Favorites remain stored in the game's existing `Favorite.json`.

POST bodies use UTF-8 `application/json`. Accepted commands return HTTP 202 with a `commandId`; poll the command endpoint or subscribe to SSE for completion. Commands are rejected with `GAME_BUSY` while OpenTaiko is outside the idle song-selection stage.

## Troubleshooting

- If the page does not open, confirm `RemoteControl.Enabled=1` and that the configured port is unused.
- The server intentionally cannot bind to a LAN address. Use it only from the same computer.
- If `PlayHistory.json` is damaged, OpenTaiko preserves it as `PlayHistory.json.corrupt-{timestamp}` and starts an empty history.
- The legacy game-event stream remains available at `/` when the request accepts `text/event-stream`; new clients should use `/api/v1/events`.
