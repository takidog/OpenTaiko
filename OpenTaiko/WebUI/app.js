const api = "/api/v1";
const ids = ["connection","stage","current-song","players","game-session","playback-status","playing-song","playing-difficulty","game-progress","elapsed-time","duration-time","session-hint","game-controls","game-retry","game-exit","song-count","playlist-count","query","genre","difficulty","min-level","max-level","favorite-only","songs","playlist","playlist-none","playlist-empty","playlist-select","playlist-create","playlist-rename","playlist-delete","playlist-export","playlist-import","playlist-dialog","playlist-dialog-song","playlist-dialog-options","playlist-dialog-create","history","error","load-more","filters","toast","web-audio","web-preview-title"];
const ui = Object.fromEntries(ids.map(id => [id, document.getElementById(id)]));
let page = 1, total = 0, loading = false, debounce, toastTimer, activePlaylistId = null, dialogSong = null;
let playlists = [];
const songsById = new Map();

async function request(path, options) {
  const response = await fetch(api + path, options);
  const contentType = response.headers.get("content-type") || "";
  const body = contentType.includes("application/json") ? await response.json().catch(() => ({})) : null;
  if (!response.ok) throw new Error(body?.error?.message || `HTTP ${response.status}`);
  return body;
}
function jsonRequest(method, path, body) {
  return request(path, { method, headers:{ "Content-Type":"application/json" }, body:JSON.stringify(body) });
}
function toast(message) {
  clearTimeout(toastTimer); ui.toast.textContent = message; ui.toast.classList.add("show");
  toastTimer = setTimeout(() => ui.toast.classList.remove("show"), 2400);
}
function setOnline(online) {
  ui.connection.textContent = online ? "已連線" : "OpenTaiko 離線";
  ui.connection.className = `pill ${online ? "online" : "offline"}`;
}
function songPath(songId, tail = "") { return `/songs/${encodeURIComponent(songId)}${tail}`; }
function activePlaylist() { return playlists.find(playlist => playlist.id === activePlaylistId) || null; }
function playlistNamesFor(songId) { return playlists.filter(playlist => playlist.songs.some(song => song.id === songId)).map(playlist => playlist.name); }

async function refreshState() {
  try {
    renderState(await request("/state")); setOnline(true);
  } catch { setOnline(false); }
}
function formatTime(milliseconds) {
  const seconds = Math.max(0, Math.floor((milliseconds || 0) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}
function renderState(state) {
  ui.stage.textContent = state.stage; ui.players.textContent = `${state.playerCount}P`;
  const title = state.songTitle || songsById.get(state.songId)?.title || state.songId || "尚未選擇";
  ui["current-song"].textContent = title;
  const visible = ["loading","playing","paused","results"].includes(state.playbackStatus);
  ui["game-session"].hidden = !visible;
  if (!visible) return;
  const labels = { loading:"載入中", playing:"正在遊玩", paused:"已暫停", results:"成績結算" };
  ui["playback-status"].textContent = labels[state.playbackStatus] || state.playbackStatus;
  ui["playing-song"].textContent = title;
  ui["playing-difficulty"].textContent = state.difficulty ? `${state.difficulty.toUpperCase()} · ${state.playerCount}P` : `${state.playerCount}P`;
  const percent = Math.max(0, Math.min(100, Math.round((state.progress || 0) * 1000) / 10));
  ui["game-progress"].style.width = `${percent}%`;
  ui["game-progress"].parentElement.setAttribute("aria-valuenow", String(percent));
  ui["elapsed-time"].textContent = formatTime(state.elapsedMs);
  ui["duration-time"].textContent = formatTime(state.durationMs);
  ui["game-controls"].hidden = state.playbackStatus === "results";
  ui["game-exit"].disabled = !state.canExit;
  ui["game-retry"].disabled = !state.canRetry;
  ui["session-hint"].textContent = state.playbackStatus === "results"
    ? "可直接在下方選取或播放另一首歌曲，不必先操作遊戲返回選歌。"
    : (!state.canExit ? "目前正在切換畫面，控制按鈕會在安全時機啟用。" : "");
}
function filterParams() {
  const params = new URLSearchParams({ page, pageSize: 40 });
  if (ui.query.value.trim()) params.set("query", ui.query.value.trim());
  if (ui.difficulty.value) params.set("difficulty", ui.difficulty.value);
  if (ui.genre.value) params.set("genre", ui.genre.value);
  if (ui["min-level"].value) params.set("minLevel", ui["min-level"].value);
  if (ui["max-level"].value) params.set("maxLevel", ui["max-level"].value);
  if (ui["favorite-only"].checked) params.set("favorite", "true");
  return params;
}
async function loadGenres() {
  const genres = await request("/genres"), selected = ui.genre.value;
  ui.genre.replaceChildren(new Option("所有分類", "")); genres.forEach(genre => ui.genre.add(new Option(genre, genre))); ui.genre.value = selected;
}
async function loadSongs(reset = false) {
  if (loading) return; loading = true;
  if (reset) { page = 1; ui.songs.replaceChildren(); }
  ui.error.hidden = true;
  try {
    const result = await request(`/songs?${filterParams()}`); total = result.total;
    result.items.forEach(song => { songsById.set(song.id, song); ui.songs.append(songCard(song)); });
    ui["song-count"].textContent = `${total} 首`; ui["load-more"].hidden = page * result.pageSize >= total;
  } catch (error) { ui.error.textContent = error.message; ui.error.hidden = false; }
  finally { loading = false; }
}

function songCard(song, mode = "catalog") {
  const card = document.getElementById("song-template").content.firstElementChild.cloneNode(true);
  card.dataset.songId = song.id; card.querySelector("h3").textContent = song.title;
  card.querySelector(".genre").textContent = song.genre || "OTHER";
  card.querySelector(".meta").textContent = [song.subtitle, song.maker].filter(Boolean).join(" · ") || song.breadcrumb;
  bindFavorite(card.querySelector(".favorite"), song);
  let selected = song.difficulties.find(d => d.id === ui.difficulty.value && d.available) || song.difficulties.find(d => d.id === "oni" && d.available) || song.difficulties.find(d => d.available);
  const holder = card.querySelector(".difficulties");
  song.difficulties.filter(d => d.available).forEach(diff => {
    const button = document.createElement("button"); button.type = "button"; button.className = "diff"; button.textContent = `${diff.id.toUpperCase()} ★${diff.level}`;
    if (diff === selected) button.classList.add("selected");
    button.onclick = () => { selected = diff; holder.querySelectorAll(".diff").forEach(item => item.classList.remove("selected")); button.classList.add("selected"); };
    holder.append(button);
  });
  const webPreview = card.querySelector(".web-preview"); webPreview.disabled = !song.webPreviewAvailable;
  webPreview.title = song.webPreviewAvailable ? "只在此網頁播放，不切換遊戲" : "此歌曲沒有可用音訊"; webPreview.onclick = () => playInBrowser(song);
  card.querySelector(".game-preview").onclick = () => command("/preview", { songId:song.id });
  card.querySelector(".select").onclick = () => selected && command("/selection", { songId:song.id, difficulty:selected.id });
  card.querySelector(".play").onclick = () => selected && command("/play", { songId:song.id, difficulty:selected.id });
  const playlistAction = card.querySelector(".playlist-action");
  if (mode === "playlist") {
    card.draggable = true;
    card.addEventListener("dragstart", event => { event.dataTransfer.setData("text/song-id", song.id); card.classList.add("dragging"); });
    card.addEventListener("dragend", () => card.classList.remove("dragging"));
    card.addEventListener("dragover", event => event.preventDefault());
    card.addEventListener("drop", event => reorderDroppedSong(event, song.id));
    playlistAction.textContent = "移出此清單"; playlistAction.classList.add("danger");
    playlistAction.onclick = () => removeFromPlaylist(activePlaylistId, song.id);
  } else {
    playlistAction.textContent = playlistNamesFor(song.id).length ? `播放清單 (${playlistNamesFor(song.id).length})` : "加入播放清單";
    playlistAction.onclick = () => openPlaylistDialog(song);
  }
  return card;
}
function bindFavorite(button, song) {
  button.dataset.songId = song.id; renderFavorite(button, song.favorite);
  button.onclick = async () => {
    const current = songsById.get(song.id) || song, next = !current.favorite; button.disabled = true;
    if (await command("/favorite", { songId:song.id, favorite:next })) {
      song.favorite = next; current.favorite = next; songsById.set(song.id, current);
      document.querySelectorAll(".favorite").forEach(candidate => { if (candidate.dataset.songId === song.id) renderFavorite(candidate, next); });
      toast(next ? "已加入最愛" : "已移除最愛"); if (ui["favorite-only"].checked && !next) loadSongs(true);
    }
    button.disabled = false;
  };
}
function renderFavorite(button, active) {
  button.textContent = active ? "♥" : "♡"; button.classList.toggle("active", active);
  button.title = active ? "移除最愛" : "加入最愛"; button.setAttribute("aria-label", button.title);
}
async function command(path, payload) {
  try {
    const result = await jsonRequest("POST", path, payload); toast(`指令已送出 · ${result.commandId.slice(0,8)}`);
    for (let attempt = 0; attempt < 30; attempt += 1) {
      await new Promise(resolve => setTimeout(resolve, 100)); const status = await request(`/commands/${result.commandId}`);
      if (status.status === "completed") return true;
      if (["rejected","failed"].includes(status.status)) throw new Error(status.errorMessage || "遊戲拒絕了指令");
    }
    return true;
  } catch (error) { toast(error.message); return false; }
}
async function playInBrowser(song) {
  try { ui["web-preview-title"].textContent = song.title; ui["web-audio"].src = api + songPath(song.id, "/audio"); await ui["web-audio"].play(); }
  catch { toast("瀏覽器無法播放這首歌曲的音訊格式"); }
}

async function loadPlaylists(preferredId = activePlaylistId) {
  try {
    playlists = await request("/playlists");
    playlists.forEach(playlist => playlist.songs.forEach(song => songsById.set(song.id, song)));
    activePlaylistId = playlists.some(item => item.id === preferredId) ? preferredId : playlists[0]?.id || null;
    renderPlaylistManager();
  } catch (error) { toast(error.message); }
}
function renderPlaylistManager() {
  ui["playlist-select"].replaceChildren(); playlists.forEach(item => ui["playlist-select"].add(new Option(`${item.name} (${item.songs.length})`, item.id)));
  if (activePlaylistId) ui["playlist-select"].value = activePlaylistId;
  const current = activePlaylist(), hasPlaylist = Boolean(current);
  ui["playlist-count"].textContent = playlists.length; ui["playlist-none"].hidden = hasPlaylist;
  ui["playlist-select"].disabled = !hasPlaylist; ui["playlist-rename"].disabled = !hasPlaylist; ui["playlist-delete"].disabled = !hasPlaylist;
  ui.playlist.replaceChildren(); ui["playlist-empty"].hidden = !hasPlaylist || current.songs.length > 0;
  if (current) current.songs.forEach(song => ui.playlist.append(songCard(song, "playlist")));
  refreshPlaylistBadges();
}
function refreshPlaylistBadges() {
  document.querySelectorAll(".playlist-badges").forEach(holder => {
    holder.replaceChildren(); playlistNamesFor(holder.dataset.songId).forEach(name => holder.append(makeBadge(name, "playlist-badge")));
  });
  document.querySelectorAll(".song .playlist-action:not(.danger)").forEach(button => {
    const songId = button.closest(".song")?.dataset.songId, count = songId ? playlistNamesFor(songId).length : 0;
    button.textContent = count ? `播放清單 (${count})` : "加入播放清單";
  });
}
async function createPlaylist() {
  const name = prompt("新播放清單名稱：", "我的清單"); if (!name?.trim()) return null;
  try { const created = await jsonRequest("POST", "/playlists", { name:name.trim() }); await loadPlaylists(created.id); toast("已新增播放清單"); return created; }
  catch (error) { toast(error.message); return null; }
}
async function renamePlaylist() {
  const current = activePlaylist(); if (!current) return;
  const name = prompt("播放清單新名稱：", current.name); if (!name?.trim() || name.trim() === current.name) return;
  try { await jsonRequest("PATCH", `/playlists/${current.id}`, { name:name.trim() }); await loadPlaylists(current.id); toast("已重新命名"); } catch (error) { toast(error.message); }
}
async function deletePlaylist() {
  const current = activePlaylist(); if (!current || !confirm(`確定刪除「${current.name}」？`)) return;
  try { await request(`/playlists/${current.id}`, { method:"DELETE" }); await loadPlaylists(null); toast("已刪除播放清單"); } catch (error) { toast(error.message); }
}
async function removeFromPlaylist(playlistId, songId) {
  if (!playlistId) return;
  try { await request(`/playlists/${playlistId}/songs/${encodeURIComponent(songId)}`, { method:"DELETE" }); await loadPlaylists(playlistId); toast("已移出播放清單"); } catch (error) { toast(error.message); }
}
async function reorderDroppedSong(event, targetSongId) {
  event.preventDefault(); const sourceSongId = event.dataTransfer.getData("text/song-id"), current = activePlaylist();
  if (!current || !sourceSongId || sourceSongId === targetSongId) return;
  const ids = current.songs.map(song => song.id), from = ids.indexOf(sourceSongId), to = ids.indexOf(targetSongId);
  if (from < 0 || to < 0) return; ids.splice(to, 0, ids.splice(from, 1)[0]);
  try { await jsonRequest("PUT", `/playlists/${current.id}/songs`, { songIds:ids }); await loadPlaylists(current.id); toast("順序已儲存"); } catch (error) { toast(error.message); }
}
function openPlaylistDialog(song) {
  dialogSong = song; ui["playlist-dialog-song"].textContent = song.title; renderPlaylistDialogOptions(); ui["playlist-dialog"].showModal();
}
function renderPlaylistDialogOptions() {
  ui["playlist-dialog-options"].replaceChildren();
  if (!playlists.length) { const empty = document.createElement("p"); empty.className = "empty"; empty.textContent = "尚無播放清單"; ui["playlist-dialog-options"].append(empty); return; }
  playlists.forEach(playlist => {
    const label = document.createElement("label"), check = document.createElement("input"), count = document.createElement("span");
    label.className = "playlist-option"; check.type = "checkbox"; check.checked = playlist.songs.some(song => song.id === dialogSong.id); count.textContent = `${playlist.songs.length} 首`;
    check.onchange = async () => {
      check.disabled = true;
      try {
        if (check.checked) await jsonRequest("POST", `/playlists/${playlist.id}/songs`, { songId:dialogSong.id });
        else await request(`/playlists/${playlist.id}/songs/${encodeURIComponent(dialogSong.id)}`, { method:"DELETE" });
        await loadPlaylists(activePlaylistId); renderPlaylistDialogOptions(); toast(check.checked ? "已加入播放清單" : "已移出播放清單");
      } catch (error) { check.checked = !check.checked; check.disabled = false; toast(error.message); }
    };
    label.append(check, document.createTextNode(playlist.name), count); ui["playlist-dialog-options"].append(label);
  });
}
async function exportPlaylists() {
  try {
    const documentValue = await request("/playlists/export"), blob = new Blob([JSON.stringify(documentValue, null, 2)], { type:"application/json" });
    const link = document.createElement("a"); link.href = URL.createObjectURL(blob); link.download = "OpenTaiko-RemotePlaylists.json"; link.click(); setTimeout(() => URL.revokeObjectURL(link.href), 1000);
  } catch (error) { toast(error.message); }
}
async function importPlaylists(file) {
  if (!file) return;
  try { const documentValue = JSON.parse(await file.text()); await jsonRequest("PUT", "/playlists/import", documentValue); await loadPlaylists(null); toast("播放清單已匯入"); }
  catch (error) { toast(`匯入失敗：${error.message}`); }
  finally { ui["playlist-import"].value = ""; }
}

function makeBadge(text, className) { const badge = document.createElement("span"); badge.className = `badge ${className}`; badge.textContent = text; return badge; }
async function loadHistory() {
  try {
    const entries = await request("/history?limit=100"); ui.history.replaceChildren();
    if (!entries.length) { ui.history.innerHTML = '<p class="empty">尚無遊玩紀錄。</p>'; return; }
    const missingIds = [...new Set(entries.map(entry => entry.songId).filter(id => !songsById.has(id)))];
    await Promise.all(missingIds.map(async id => { try { songsById.set(id, await request(songPath(id))); } catch { /* removed song */ } }));
    entries.forEach(entry => {
      const row = document.createElement("div"), song = songsById.get(entry.songId); row.className = "history-item";
      const copy = document.createElement("div"), heading = document.createElement("div"), title = document.createElement("strong"), detail = document.createElement("small"), badges = document.createElement("div"), playlistBadges = document.createElement("span"), actions = document.createElement("div");
      copy.className = "history-copy"; heading.className = "history-heading"; badges.className = "badges"; playlistBadges.className = "playlist-badges"; playlistBadges.dataset.songId = entry.songId; actions.className = "history-actions";
      title.textContent = entry.songTitle || song?.title || (entry.difficulty === "dan" ? "段位道場" : "未知歌曲"); detail.textContent = `${entry.difficulty.toUpperCase()} · ${new Date(entry.startedAtUtc).toLocaleString()}`;
      const genreName = entry.genre || song?.genre; if (genreName) badges.append(makeBadge(genreName, "genre-badge")); badges.append(playlistBadges);
      playlistNamesFor(entry.songId).forEach(name => playlistBadges.append(makeBadge(name, "playlist-badge")));
      heading.append(title); if (song) { const favorite = document.createElement("button"); favorite.type = "button"; favorite.className = "favorite icon-button history-heart"; bindFavorite(favorite, song); heading.append(favorite); }
      copy.append(heading, badges, detail);
      const webPreview = document.createElement("button"); webPreview.className = "secondary"; webPreview.textContent = "網頁試聽"; webPreview.disabled = !song?.webPreviewAvailable; webPreview.onclick = () => song && playInBrowser(song);
      const gamePreview = document.createElement("button"); gamePreview.className = "secondary"; gamePreview.textContent = "遊戲 Preview"; gamePreview.disabled = !song; gamePreview.onclick = () => command("/preview", { songId:entry.songId });
      const play = document.createElement("button"); play.textContent = "再玩一次"; play.onclick = () => command(`/history/${entry.historyId}/play`, {});
      actions.append(webPreview, gamePreview, play); row.append(copy, actions); ui.history.append(row);
    });
  } catch { /* History is optional while the game starts. */ }
}

document.querySelectorAll(".tab").forEach(tab => tab.onclick = () => {
  document.querySelectorAll(".tab").forEach(item => item.classList.toggle("active", item === tab));
  document.querySelectorAll(".tab-panel").forEach(panel => { const active = panel.id === tab.dataset.tab; panel.hidden = !active; panel.classList.toggle("active", active); });
  if (tab.dataset.tab === "playlist-panel") loadPlaylists(); if (tab.dataset.tab === "history-panel") loadHistory();
});
ui.filters.addEventListener("input", () => { clearTimeout(debounce); debounce = setTimeout(() => loadSongs(true), 220); });
ui["load-more"].onclick = () => { page += 1; loadSongs(); };
ui["playlist-select"].onchange = () => { activePlaylistId = ui["playlist-select"].value; renderPlaylistManager(); };
ui["playlist-create"].onclick = createPlaylist; ui["playlist-rename"].onclick = renamePlaylist; ui["playlist-delete"].onclick = deletePlaylist;
ui["playlist-export"].onclick = exportPlaylists; ui["playlist-import"].onchange = () => importPlaylists(ui["playlist-import"].files[0]);
ui["playlist-dialog-create"].onclick = async () => { const created = await createPlaylist(); if (created) renderPlaylistDialogOptions(); };
ui["game-exit"].onclick = async () => { ui["game-exit"].disabled = true; ui["game-retry"].disabled = true; await command("/gameplay/exit", {}); refreshState(); };
ui["game-retry"].onclick = async () => { ui["game-exit"].disabled = true; ui["game-retry"].disabled = true; await command("/gameplay/retry", {}); refreshState(); };

async function start() {
  try { await request("/health"); setOnline(true); await loadGenres(); await loadPlaylists(); await loadSongs(true); await Promise.all([refreshState(), loadHistory()]); }
  catch { setOnline(false); ui.error.textContent = "無法連接 OpenTaiko。請確認 RemoteControl.Enabled=1。"; ui.error.hidden = false; }
  const events = new EventSource(`${api}/events`); events.onmessage = event => {
    try {
      const message = JSON.parse(event.data);
      if (message.type === "state") { renderState(message.data); setOnline(true); }
      if (message.type === "history") loadHistory();
    } catch { refreshState(); }
  }; events.onerror = () => setOnline(false);
  setInterval(refreshState, 3000);
}
start();
