# OpenTaiko 快速啟動與遠端選曲開發規劃

## 1. 文件目的

本文件把下列需求拆成可由多個 Agent 分工實作、測試及整合的工作項目：

1. 可停用開頭的網路連線檢查。
2. 可從設定檔指定預設存檔、1P/2P 人數與玩家側，並選擇是否跳過標題選人流程。
3. 提供 HTTP API 查詢歌曲、試聽、選歌、選難度、開始遊玩、重新遊玩及讀取最近 100 次歷史。
4. 提供透過 API 操作的快速選歌前端。

規劃以目前 `main` 分支 `7b832277b79b8c72258c954db8f09cba9d618ca5` 為基準。

## 2. 現況調查

### 2.1 已有能力

- 專案是 .NET 8 WinExe，主要專案為 `OpenTaiko/OpenTaiko.csproj`。
- `src/Helpers/HttpEventReporter.cs` 已使用 `HttpListener`，但目前只把所有連線當成 SSE client，沒有 REST 路由。
- `CConfigIni` 已有 `PlayerCount`、`bEnableGameEventBroadcasting`、`nGameEventBroadcastingPort` 與 Discord 顯示設定。
- `CSongMount` 已保存目前選取及正式確認的歌曲、難度和譜面狀態。
- `CSongDict` 以歌曲 unique ID 建立索引，但目前沒有安全的唯讀完整列舉介面。
- `RecentlyPlayedSongs` 只保存歌曲 ID、預設上限為 5，重複遊玩不會產生新紀錄，且沒有難度或時間，因此不足以支援精確的「再玩一次」。

### 2.2 目前限制與風險

- `OpenTaiko.cs` 約每 10 秒 ping `8.8.8.8`；離線時不應讓此工作持續發生，應該要可以在config跳過。
- Discord RPC 目前仍會初始化，不能只靠「顯示正在玩的歌曲」設定完整停用外部連線。
- 啟動後仍進入 `CStageTitle`，存檔、玩家側、人數與模式選擇具有動畫及狀態依賴，不能只模擬按鍵跳過。
- `HttpEventReporter` 的 listener thread 不能直接改 `CSongMount` 或 stage；這些狀態必須在遊戲主執行緒修改。
- 表譜 Oni 與裏譜 Edit/Ura 的 UI 流程有額外狀態。API 必須透過共用選曲服務進入遊戲，不可只寫入難度欄位。
- 前端若可從區域網路存取，就等同可遠端控制遊戲；預設只綁定 loopback，使用者可明確設定 `Host=0.0.0.0` 開放可信任區網。

## 3. 產品行為決策

### 3.1 啟動流程

- 不跳過必要的資源、Skin、字型及曲目掃描，只跳過網路探測與可選的標題互動。
- 預設保留上游行為，使用者明確啟用快速啟動後才直接進入選曲。
- 快速啟動會先載入設定指定的存檔，再套用玩家數與玩家側；設定無效時寫 log 並安全回到標題畫面。
- 「1P/2P」拆成三個不同概念，避免設定名稱混淆：
  - `DefaultSaveSlot`：使用哪個存檔。
  - `PlayerCount`：實際遊玩人數，沿用既有設定。
  - `DefaultPlayerSide`：單人時使用左側或右側玩家位置。

### 3.2 網路模式

- `EnableNetworkConnectivityCheck=false` 時不建立 ping task，也不顯示錯誤的連線狀態提示。
- `EnableDiscordRpc=false` 時完全不初始化 Discord client。
- 本機 API 是使用者主動開啟的功能，不受 `EnableNetworkConnectivityCheck` 影響。
- API 預設 `127.0.0.1`；設定 `Host=0.0.0.0` 時綁定所有網路介面，依需求不加入 token 或配對驗證，且文件必須警告只可用於可信任私人網路。

### 3.3 「播放歌曲」定義

為避免前端與 Agent 對「播放」理解不同，第一版同時提供：

- Preview：播放歌曲的試聽區段，不進入譜面。
- Play：確認歌曲及難度，進入譜面載入及正式遊玩。

## 4. 建議設定

沿用目前設定序列化方式，新增下列鍵；預設值必須維持原版使用體驗：

```ini
[Startup]
SkipTitleScreen=0
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

驗證規則：

- `DefaultSaveSlot` 先限制在目前實際支援的存檔範圍；不存在時退回 1。
- `PlayerCount` 使用既有合法範圍，第一版驗收至少涵蓋 1 與 2。
- `DefaultPlayerSide` 僅接受 `Left` 或 `Right`。
- `Port` 必須是 1 到 65535；占用時顯示清楚 log，但不可阻止遊戲啟動。
- `MaxEntries` 第一版固定或限制為 1 到 100，避免無界資料成長。

## 5. 架構設計

### 5.1 元件切分

建議新增以下邏輯元件，實際 namespace 依專案慣例調整：

```text
src/RemoteControl/
  HttpControlServer.cs
  ApiRouter.cs
  ApiContracts.cs
  RemoteCommandQueue.cs
  SongCatalogSnapshot.cs
  SongSelectionService.cs
  PlayHistoryService.cs
  StaticFileHandler.cs
WebUI/
  package.json
  src/
```

- `HttpControlServer`：listener 生命週期、連線與回應管理。
- `ApiRouter`：method/path 驗證、JSON 解析、狀態碼與錯誤格式。
- `RemoteCommandQueue`：HTTP thread 到遊戲主 thread 的唯一寫入通道。
- `SongCatalogSnapshot`：曲目載入完成後建立不可變 DTO 快照，供查詢 thread 使用。
- `SongSelectionService`：封裝正常 UI 與 API 共用的選歌、選難度及開始遊玩邏輯。
- `PlayHistoryService`：最多 100 筆、原子寫入、損毀復原及查詢。
- `StaticFileHandler`：只提供編譯完成的前端資源，必須防止 path traversal。

### 5.2 Threading 規則

1. GET 類 API 只能讀取不可變 snapshot 或 lock 保護的資料。
2. POST 類控制 API 驗證成功後放入 `ConcurrentQueue<RemoteCommand>`，回傳 `202 Accepted` 與 `commandId`。
3. 遊戲每幀在固定位置 drain queue，並在主執行緒呼叫 `SongSelectionService`。
4. 指令完成、拒絕或失敗後更新 command result，並透過 SSE 發送狀態。
5. HTTP handler 絕對不能直接切換 stage、修改 `CSongMount` 或操作音效物件。

### 5.3 狀態與衝突規則

- `select` 只在歌曲資料已載入且位於可安全返回選曲的狀態執行。
- `play` 僅在選曲 stage 接受；遊玩中、結果畫面或資源載入中回傳 `409 GAME_BUSY`。
- `restart` 使用歷史紀錄的歌曲 ID、難度及遊玩設定；曲目已刪除時回傳 `404 SONG_NOT_FOUND`。
- 第一版不提供強制中斷目前譜面。若日後需要，另設明確的 `force` API 與確認機制。
- Ura 使用 API 字串 `ura`，內部映射到 `Difficulty.Edit`，不把 enum 數字暴露成公共契約。

## 6. HTTP API v1

Base URL：本機為 `http://127.0.0.1:{port}/api/v1`；區網模式為 `http://<遊戲電腦區網 IP>:{port}/api/v1`。

所有 JSON 使用 UTF-8。成功格式可直接回傳資源；錯誤統一為：

```json
{
  "error": {
    "code": "INVALID_DIFFICULTY",
    "message": "The selected chart does not contain the requested difficulty.",
    "details": {}
  }
}
```

### 6.1 查詢

| Method | Path | 用途 |
| --- | --- | --- |
| GET | `/health` | listener、版本與歌曲索引是否就緒 |
| GET | `/state` | stage、目前歌曲、目前難度、player count 與 command 狀態 |
| GET | `/songs` | 分頁、搜尋與篩選歌曲 |
| GET | `/songs/{songId}` | 取得單曲與可用難度 |
| GET | `/history?limit=100` | 由新到舊取得最近遊玩紀錄 |
| GET | `/commands/{commandId}` | 查詢非同步指令結果 |
| GET | `/events` | SSE：stage、selection、history 與 command 更新 |

`GET /songs` 建議參數：

```text
query, genre, difficulty, minLevel, maxLevel, page, pageSize
```

`pageSize` 預設 100、最大 500。搜尋至少涵蓋各語系標題、subtitle、maker 與 genre。

### 6.2 控制

| Method | Path | Body | 用途 |
| --- | --- | --- | --- |
| POST | `/selection` | `songId`, `difficulty` | 在遊戲選曲 UI 定位並選擇歌曲/難度 |
| POST | `/play` | `songId`, `difficulty`, `playerCount?` | 選擇並進入正式譜面 |
| POST | `/preview` | `songId` | 播放該歌曲試聽段落 |
| POST | `/preview/stop` | 無 | 停止試聽 |
| POST | `/restart` | `historyId?` | 重玩指定歷史；省略時重玩最近一筆 |
| POST | `/history/{historyId}/play` | 無 | 重玩指定歷史的別名 |

難度 API 值固定為：`easy`、`normal`、`hard`、`oni`、`ura`、`tower`、`dan`。不存在或不可用的難度回傳 422。

### 6.3 Song DTO

```json
{
  "id": "stable-song-unique-id",
  "title": "顯示標題",
  "titles": { "ja": "...", "zh-tw": "..." },
  "subtitle": "...",
  "genre": "Anime",
  "maker": "...",
  "breadcrumb": "Anime/Folder/Song",
  "difficulties": [
    { "id": "oni", "level": 9, "available": true },
    { "id": "ura", "level": 10, "available": true }
  ],
  "favorite": false
}
```

API 不回傳本機絕對路徑、TJA 原文或音訊檔案路徑，避免不必要的資料外洩及任意檔案存取風險。

## 7. 遊玩歷史

新增 `PlayHistory.json`，不要直接擴充舊 `RecentlyPlayedSongs.json`，以維持既有 Recent Songs 資料夾相容性。

每筆建議格式：

```json
{
  "historyId": "uuid",
  "songId": "stable-song-unique-id",
  "difficulty": "ura",
  "saveSlot": 1,
  "playerCount": 1,
  "playerSide": "left",
  "startedAtUtc": "2026-09-15T12:00:00Z",
  "completedAtUtc": null,
  "status": "started",
  "modifiers": {}
}
```

資料規則：

- 譜面正式開始載入時新增紀錄，使當機或中途退出的歌曲仍可快速回去。
- 結果 stage 更新同一筆紀錄的 `completedAtUtc`、status、分數與 clear 結果。
- 相同歌曲與難度可以重複出現，因為這是遊玩事件歷史，不是最近歌曲集合。
- 超過 100 筆時刪除最舊項目。
- 使用 temp file + replace 原子寫入；JSON 壞掉時保留 `.corrupt-{timestamp}` 備份並建立空資料。
- 舊 `RecentlyPlayedSongs` 繼續由原流程維護，之後可改由 history service 投影產生，但不列入第一版必要範圍。

## 8. 快速選歌前端

### 8.1 技術方向

- 建議使用 TypeScript + Vite 的輕量 SPA；若不希望引入 React，第一版可用原生 TypeScript。
- Release build 將前端輸出複製到遊戲的 `WebUI/`，由相同 port 提供，避免 CORS 與額外服務程序。
- UI 透過 REST 讀寫，透過 `/events` SSE 即時同步遊戲狀態及指令結果。
- 前端編譯工具只存在開發階段；Release 使用靜態 HTML/CSS/JS，不要求玩家安裝 Node.js。

### 8.2 第一版畫面

- 頂部：連線狀態、目前 stage、目前播放/選取歌曲。
- 搜尋列：標題、作者、資料夾即時搜尋。
- 篩選：genre、難度、星數。
- 曲目清單：虛擬化或分頁，顯示標題、genre 及各難度星數。
- 操作：試聽、停止、選取、開始。
- 最近遊玩：最多 100 筆，可一鍵重玩相同歌曲及難度。
- 手機版：主要操作在單欄完成，按鈕尺寸適合觸控。

### 8.3 前端錯誤處理

- API 未啟用或 OpenTaiko 未啟動時顯示可理解的離線狀態。
- `202` 後顯示等待中，直到 SSE 或 command query 回報完成。
- `409` 顯示目前遊戲忙碌，不自動重試開始遊玩命令。
- 曲目重新掃描後 ID 消失時移除失效選取並提示使用者。

## 9. Agent 工作包

先由整合負責人確認本文件中的設定名稱與 API v1 契約，再允許各 Agent 平行工作。共用契約檔合併後，其他 Agent 不應自行更名 endpoint 或 DTO 欄位。

### Agent A：啟動與設定

範圍：`CConfigIni`、設定 UI、`OpenTaiko.cs` 網路/Discord 初始化、startup/title stage 轉移。

交付：

- 新設定可讀、寫、驗證並保留預設舊行為。
- 完整停用 ping 與 Discord RPC。
- 快速啟動可依設定載入存檔、人數與玩家側後進入選曲。
- 快速啟動失敗會退回標題，不造成黑畫面或未初始化狀態。

避免修改：HTTP router、歷史格式、前端。

### Agent B：歌曲目錄與選曲服務

範圍：`CSongDict`、`CSongMount`、`CStageSongSelect`、難度選擇及正式確認流程。

交付：

- 安全、不可變的歌曲 DTO snapshot。
- 從既有 UI 確認流程抽出的 `SongSelectionService`。
- 完整支援 Oni 與 Ura/Edit，不用模擬鍵盤輸入。
- select、play、preview、stop 的 domain result 與錯誤碼。

避免修改：listener、設定序列化、前端。

### Agent C：HTTP 與命令佇列

範圍：`HttpEventReporter` 遷移/相容層、`HttpControlServer`、router、command queue、SSE。

交付：

- `/api/v1` endpoints、UTF-8 JSON、統一錯誤格式。
- 寫入指令只經主執行緒 queue。
- loopback bind、body size、method、content type 與 path traversal 防護。
- 保留現有遊戲事件廣播能力，舊 consumer 若有使用應提供遷移說明。

依賴：共用 DTO/command contract；可先用 mock service 開發。

### Agent D：最近 100 次歷史

範圍：新 `PlayHistoryService`、遊戲開始 hook、結果 stage hook、restart command resolver。

交付：

- 新增、完成、裁切、載入、原子寫入及損毀復原。
- 100 筆限制與相同歌曲重複紀錄。
- 精確保存 difficulty，尤其是 Ura。
- 不破壞現有 `RecentlyPlayedSongs.json`。

依賴：共用 History DTO；與 Agent B 對接 restart payload。

### Agent E：Web 前端

範圍：`WebUI/` 及 publish 靜態資源整合。

交付：

- 搜尋、篩選、分頁/虛擬化、難度選擇、試聽、開始、歷史重玩。
- REST client、SSE reconnect、loading/error/busy 狀態。
- 桌面及手機瀏覽器可用。
- 不依賴本機絕對檔案路徑。

依賴：固定 API contract；可先使用 mock JSON 開發。

### Agent F：整合與 QA

範圍：跨模組接線、測試、自動建置、文件及 release packaging。

交付：

- 解決 Agent 分支衝突，不重新設計已凍結的 API。
- 建置時自動產生或複製 WebUI 靜態輸出。
- 完成下方驗收矩陣與回歸測試。
- 更新 README：設定範例、API URL、前端網址及故障排除。

## 10. 建議實作順序

1. 建立 DTO、error code、command types 與 API contract tests，凍結 v1 契約。
2. Agent A、B、D 平行處理啟動、選曲 domain 與歷史儲存。
3. Agent C 以 mock service 建立 HTTP/API，再接 Agent B/D 的實作。
4. Agent E 以 mock server 開發，契約穩定後接真實 API。
5. Agent F 合併、執行離線與 Ura 情境測試、修正 packaging。

建議各工作包各自使用 feature branch，避免直接推送 `main`：

```text
feature/startup-config
feature/song-control-service
feature/http-api
feature/play-history
feature/quick-select-web
integration/remote-control-v1
```

## 11. 測試與驗收

### 11.1 自動測試

- 設定 round-trip：新增鍵缺省、合法值、非法值 fallback。
- Song DTO：多語標題、缺少譜面、同時有 Oni/Ura、特殊 Unicode。
- Router：method、404、422、409、body 過大、壞 JSON、URL encoded song ID。
- Command queue：背景 thread 入列、主 thread 執行、順序及例外隔離。
- History：0、1、100、101 筆、重複歌曲、壞檔、原子替換失敗。
- Static files：未知檔案、fallback、`..` traversal 與非 GET method。

若目前 repo 沒有合適測試專案，先新增獨立 `OpenTaiko.Tests`，優先測試不依賴圖形裝置的 service 與 contract。

### 11.2 手動端到端驗收

- 拔除網路後啟動，未產生 ping/Discord 嘗試，仍可正常掃歌及遊玩。
- 設定單人、指定存檔與跳過標題後，啟動直接抵達可操作選曲畫面。
- 設定雙人後，兩名玩家的難度與載入流程均正常。
- API disabled 時不監聽 port，遊戲行為與上游一致。
- API enabled 時前端可讀取完整曲庫，不因日文、繁中或特殊符號亂碼。
- 選擇同時具有 Oni/Ura 的歌曲，兩種難度各自可正確進入並載入相應譜面。
- 試聽不改寫遊玩歷史；正式開始才新增歷史。
- 連續遊玩 101 次後只保留最新 100 次，順序正確。
- 從歷史重玩可回到同一 song ID 與 difficulty。
- 遊玩中送出另一個 play，API 回 409，遊戲不中斷也不閃退。
- Release publish 後不安裝 Node.js 也能打開快速選歌頁。

## 12. 完成定義

以下條件全部滿足才算第一版完成：

- 四項使用者需求都有可重現的驗收步驟。
- 新功能預設關閉或保持既有 OpenTaiko 行為，不影響一般玩家。
- 所有遊戲狀態修改都在主執行緒發生。
- API 只綁定 loopback，沒有任意檔案讀取或路徑穿越。
- Ura/Edit 經 API 選擇、開始及歷史重玩均通過測試。
- 遊玩歷史在異常關閉與 JSON 損毀後能安全恢復。
- Windows x64 Release build 成功，WebUI 資源包含在 publish 產物。
- README、設定範例與 API 文件已更新。

## 13. 整合前需確認但不阻塞開工的項目

- `DefaultSaveSlot` 的產品語意是否就是目前標題畫面的 1P/2P 存檔選擇；本規劃先按此實作，並把實際遊玩人數保留為獨立 `PlayerCount`。
- 試聽是否要遵循 TJA 的 demo start、音量與目前 BGM ducking；第一版應直接重用遊戲現有試聽行為。
- 是否需要手機從同一區域網路控制。第一版刻意不支援；若需要，必須另做 bind allowlist、token、配對與 CSRF/CORS 設計。
- 是否保留舊 SSE 根路徑。若已有外部 consumer，建議暫時相容並標示 deprecated；新前端一律使用 `/api/v1/events`。

## 14. 後續需求：已確認並進入實作

以下兩項缺失已於 2026-09-16 由使用者確認開始實作。

### 14.1 啟動時固定玩家選擇

**問題：** 啟動遊戲時仍會出現 `Press P to select 1P/2P`，即使使用者已經知道要以哪一種模式遊玩。

**規劃：**

- 新增明確的設定鍵，將啟動時的玩家模式固定為 `1P` 或 `2P`，例如 `Startup.PlayerMode=1P`。
- 啟用固定模式後，啟動流程直接採用設定值，不再等待 `Press P to select 1P/2P`。
- 保留 `PlayerCount`、`DefaultSaveSlot`、`DefaultPlayerSide` 的既有語意；玩家模式只負責消除啟動互動，不能覆蓋不相容的人數設定。
- 設定缺失或值非法時維持目前原版互動流程，並寫入清楚的 log。
- 驗收涵蓋 1P、2P、非法值 fallback，以及一般玩家未啟用設定時的回歸行為。

**已確認：** 採用 `Startup.PlayerMode`，並同步套用 `PlayerCount`。

### 14.2 獨立的網頁播放清單管理

**問題：** 目前網頁只有一個播放清單，且操作概念仍接近遊戲本體的歌曲集合；使用者需要由網頁自行建立、修改、刪除播放清單，並把歌曲加入指定清單。

**規劃：**

- 網頁播放清單完全獨立於 OpenTaiko 原有的 Favorite、Recent、`X1 Favorite` 及 `X2 Recent` 資料夾架構。
- 新增播放清單資料模型：清單 ID、名稱、建立時間、更新時間、歌曲 ID 順序及必要的排序資訊。
- 支援建立清單、重新命名、刪除清單、清單排序，以及將歌曲加入／移出指定清單；同一首歌在不同清單中可以同時存在。
- 預設提供一個可刪除或重新命名的清單（產品決策待確認），不把遊戲 Favorite 自動轉換成網頁清單。
- 清單資料獨立持久化於遊戲目錄的專用檔案（建議 `RemotePlaylists.json`），採原子寫入、損毀備份與無效歌曲清理；不修改 `Favorite.json`、`RecentlyPlayedSongs.json` 或 `Saves.db3`。
- API 以清單資源為中心，建議提供：
  - `GET /playlists`
  - `POST /playlists`
  - `PATCH /playlists/{playlistId}`
  - `DELETE /playlists/{playlistId}`
  - `POST /playlists/{playlistId}/songs`
  - `DELETE /playlists/{playlistId}/songs/{songId}`
  - `PUT /playlists/{playlistId}/songs`（一次更新歌曲順序，可選）
- 歌曲與最近遊玩介面顯示所屬清單 badge；若屬於多個清單，顯示多個清單名稱或可展開的 badge 群組。
- 前端提供清單管理 UI；加入歌曲時可選清單，而不是只有「加入唯一清單」按鈕。
- 清單 CRUD 不依賴遊戲目前是否停在 SongSelect；只有選取、遊戲 Preview、開始遊玩等遊戲狀態命令仍遵守 `GAME_BUSY` 規則。

**已確認：** 清單跨存檔共用，支援拖曳排序與 JSON 匯入／匯出，並允許刪除最後一個清單。

### 14.3 本階段完成條件

- 以上兩項需求先由使用者確認設定名稱、模式語意及播放清單產品規則。
- 使用者確認後，才建立獨立 commit、補 API／持久化／前端測試，並重新編譯部署到正式環境。
- 在確認前，現有已部署版本維持不變；本節僅作為後續開發規劃與缺失追蹤。

## 15. 後續需求：網頁瀏覽、遊玩控制與結算切歌改善（待實作）

以下需求於 2026-09-16 記錄，先納入後續開發範圍，本階段不立即實作。

### 15.1 歌曲列表改為無限下滑

**問題：** 歌曲數量多時，手動切換頁碼會打斷瀏覽與選歌流程。

**規劃：**

- 移除使用者需要操作的上一頁、下一頁及頁碼控制，改為捲動接近列表底部時自動載入下一批歌曲。
- 沿用 API 的 `page`／`pageSize` 分批查詢能力，前端負責累加結果，不一次把完整曲庫建立成大量 DOM 節點。
- 搜尋文字、分類、難度、等級或最愛條件改變時，取消尚未完成的舊請求、清空目前結果並從第一批重新載入。
- 防止同一批資料重複載入，並明確呈現載入中、已無更多歌曲及請求失敗後重試等狀態。
- 桌面與手機版都必須保留合理的捲動位置與操作效能；切換歌曲列表、播放清單、最近遊玩 tab 時不得錯載其他 tab 的資料。

### 15.2 過長曲名與作者名不得造成跑版

**問題：** 曲名或作者名過長時會超出歌曲卡片，撐大整個頁面並產生水平捲動。

**規劃：**

- 歌曲卡片及其 grid/flex 子元素必須允許縮小，長文字使用省略號或固定行數截斷，不得改變整頁寬度。
- 曲名可顯示最多兩行、作者名最多一行；滑鼠停留、可存取標籤或詳細資料區仍能查看完整文字。
- 對沒有空白的長字串、日文、繁中、英文及混合 Unicode 曲名加入專門測試。
- 手機窄螢幕、桌面瀏覽器及播放清單／最近遊玩卡片必須套用一致的防溢出規則。

### 15.3 網頁即時顯示目前遊玩狀態與歌曲進度

**問題：** 網頁送出播放指令後，只知道指令是否被接受，無法持續確認遊戲是否已載入、正在遊玩、已暫停或已進入結算。

**規劃：**

- 擴充遊戲狀態 snapshot 與 SSE `state` 事件，至少提供階段、歌曲 ID、曲名、難度、遊玩狀態、已播放時間、歌曲／譜面總時間及可計算的進度百分比。
- 狀態至少區分 `loading`、`playing`、`paused`、`results`、`songSelect`；前端斷線重連後可由 `GET /state` 立即恢復畫面。
- 前端新增固定或醒目的「正在遊玩」區塊，顯示歌曲、難度、時間與進度條，不依賴使用者停留在特定 tab。
- 進度更新應有節流頻率，避免每幀透過 SSE 傳送；時間誤差需控制在使用者可感知的合理範圍內。
- 不將遊玩中的可變遊戲物件直接交給 HTTP thread；仍由主執行緒產生不可變 snapshot。

### 15.4 遊玩中的大型控制按鈕

**需求：** 網頁在遊玩期間提供明顯且容易在手機操作的「退出」及「重試」按鈕。

**規劃：**

- 「退出」會安全結束目前遊玩並回到選歌畫面，不關閉遊戲程式。
- 「重試」使用目前歌曲、難度與必要的玩家設定重新開始，不需要先返回選歌。
- 新增或調整遠端命令契約，所有 stage 切換仍由遊戲主執行緒執行；命令需回傳 queued、completed、rejected 或 failed 狀態。
- 只有在支援的遊玩階段顯示／啟用按鈕；載入中、淡入淡出或其他不可安全中斷的短暫階段，前端需停用按鈕並顯示原因。
- 按鈕採適合觸控的大尺寸設計，並防止連點重複送出相同命令。

### 15.5 結算畫面可由網頁直接選歌

**問題：** 目前進入成績結算後，API 選歌會因不在 SongSelect 而拒絕，使用者必須先在遊戲端手動退出結算。

**規劃：**

- 結算階段接受網頁的選歌／播放要求，由主執行緒安全離開結算並切回選歌，再套用目標歌曲與難度。
- 使用者點擊「選取」時停在目標歌曲的選歌畫面；點擊「播放」時可在完成必要 stage 轉換後直接載入目標譜面。
- 命令在整個跨 stage 流程完成前保持可追蹤狀態；若歌曲不存在、難度無效或 stage 轉換失敗，回傳明確錯誤，不能停在不一致狀態。
- 保留剛完成的成績與歷史紀錄，不因遠端離開結算而漏寫或重複寫入。
- 不把此能力擴大成任意 stage 的強制切歌；第一階段只允許 SongSelect 與成績結算等已明確驗證的安全階段。

### 15.6 建議實作順序與驗收

1. 先修正文字溢出並將歌曲列表改成無限下滑，補齊前端搜尋重設及競態測試。
2. 擴充遊玩狀態 DTO、`GET /state` 與 SSE，再加入網頁進度顯示。
3. 實作遊玩中退出／重試命令及大型控制按鈕。
4. 實作結算畫面跨 stage 選歌／播放流程，最後進行雙端整合測試。

完成驗收至少包含：大量歌曲連續下滑無重複或漏載、極長曲名／作者不產生水平跑版、網頁進度可隨遊戲即時更新、退出與重試在單人正常譜面可可靠執行、結算時能直接選取或播放另一首歌曲，以及 API／SSE 斷線重連後狀態可恢復。
