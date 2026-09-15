const api = "/api/v1";
const ids = ["connection","stage","current-song","players","song-count","playlist-count","query","genre","difficulty","min-level","max-level","favorite-only","songs","playlist","playlist-empty","history","error","load-more","filters","toast","web-audio","web-preview-title"];
const ui = Object.fromEntries(ids.map(id => [id, document.getElementById(id)]));
let page = 1, total = 0, loading = false, debounce, toastTimer;
const songsById = new Map();
const playlistSongIds = new Set();

async function request(path, options) {
  const response = await fetch(api + path, options);
  const contentType = response.headers.get("content-type") || "";
  const body = contentType.includes("application/json") ? await response.json().catch(() => ({})) : null;
  if (!response.ok) throw new Error(body?.error?.message || `HTTP ${response.status}`);
  return body;
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

async function refreshState() {
  try {
    const state = await request("/state");
    setOnline(true); ui.stage.textContent = state.stage; ui.players.textContent = `${state.playerCount}P`;
    ui["current-song"].textContent = songsById.get(state.songId)?.title || state.songId || "尚未選擇";
  } catch { setOnline(false); }
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
  const genres = await request("/genres");
  const selected = ui.genre.value;
  ui.genre.replaceChildren(new Option("所有分類", ""));
  genres.forEach(genre => ui.genre.add(new Option(genre, genre)));
  ui.genre.value = selected;
}

async function loadSongs(reset = false) {
  if (loading) return; loading = true;
  if (reset) { page = 1; ui.songs.replaceChildren(); }
  ui.error.hidden = true;
  try {
    const result = await request(`/songs?${filterParams()}`); total = result.total;
    result.items.forEach(song => { songsById.set(song.id, song); ui.songs.append(songCard(song)); });
    ui["song-count"].textContent = `${total} 首`;
    ui["load-more"].hidden = page * result.pageSize >= total;
  } catch (error) { ui.error.textContent = error.message; ui.error.hidden = false; }
  finally { loading = false; }
}

function songCard(song, mode = "catalog") {
  const card = document.getElementById("song-template").content.firstElementChild.cloneNode(true);
  card.dataset.songId = song.id;
  card.querySelector("h3").textContent = song.title;
  card.querySelector(".genre").textContent = song.genre || "OTHER";
  card.querySelector(".meta").textContent = [song.subtitle, song.maker].filter(Boolean).join(" · ") || song.breadcrumb;
  const favorite = card.querySelector(".favorite");
  bindFavorite(favorite, song);

  let selected = song.difficulties.find(d => d.id === ui.difficulty.value && d.available)
    || song.difficulties.find(d => d.id === "oni" && d.available)
    || song.difficulties.find(d => d.available);
  const holder = card.querySelector(".difficulties");
  song.difficulties.filter(d => d.available).forEach(diff => {
    const button = document.createElement("button"); button.type = "button"; button.className = "diff";
    button.textContent = `${diff.id.toUpperCase()} ★${diff.level}`;
    if (diff === selected) button.classList.add("selected");
    button.addEventListener("click", () => {
      selected = diff; holder.querySelectorAll(".diff").forEach(x => x.classList.remove("selected")); button.classList.add("selected");
    });
    holder.append(button);
  });

  const webPreview = card.querySelector(".web-preview");
  webPreview.disabled = !song.webPreviewAvailable;
  webPreview.title = song.webPreviewAvailable ? "只在此網頁播放，不切換遊戲" : "此歌曲沒有可用音訊";
  webPreview.onclick = () => playInBrowser(song);
  card.querySelector(".game-preview").onclick = () => command("/preview", { songId: song.id });
  card.querySelector(".select").onclick = () => selected && command("/selection", { songId: song.id, difficulty: selected.id });
  card.querySelector(".play").onclick = () => selected && command("/play", { songId: song.id, difficulty: selected.id });

  const playlistAction = card.querySelector(".playlist-action");
  if (mode === "playlist") {
    playlistAction.textContent = "移出清單"; playlistAction.classList.add("danger");
    playlistAction.onclick = () => removeFromPlaylist(song.id);
  } else {
    playlistAction.onclick = () => addToPlaylist(song.id);
  }
  return card;
}

function bindFavorite(button, song) {
  button.dataset.songId = song.id;
  renderFavorite(button, song.favorite);
  button.onclick = async () => {
    const current = songsById.get(song.id) || song;
    const next = !current.favorite;
    button.disabled = true;
    if (await command("/favorite", { songId: song.id, favorite: next })) {
      song.favorite = next; current.favorite = next; songsById.set(song.id, current);
      document.querySelectorAll(".favorite").forEach(candidate => {
        if (candidate.dataset.songId === song.id) renderFavorite(candidate, next);
      });
      toast(next ? "已加入最愛" : "已移除最愛");
      if (ui["favorite-only"].checked && !next) loadSongs(true);
    }
    button.disabled = false;
  };
}

function renderFavorite(button, active) {
  button.textContent = active ? "♥" : "♡";
  button.classList.toggle("active", active);
  button.title = active ? "移除最愛" : "加入最愛";
  button.setAttribute("aria-label", button.title);
}

async function command(path, payload) {
  try {
    const result = await request(path, { method:"POST", headers:{ "Content-Type":"application/json" }, body:JSON.stringify(payload) });
    toast(`指令已送出 · ${result.commandId.slice(0,8)}`);
    for (let attempt = 0; attempt < 30; attempt += 1) {
      await new Promise(resolve => setTimeout(resolve, 100));
      const status = await request(`/commands/${result.commandId}`);
      if (status.status === "completed") return true;
      if (status.status === "rejected" || status.status === "failed") throw new Error(status.errorMessage || "遊戲拒絕了指令");
    }
    return true;
  } catch (error) { toast(error.message); return false; }
}

async function playInBrowser(song) {
  try {
    ui["web-preview-title"].textContent = song.title;
    ui["web-audio"].src = api + songPath(song.id, "/audio");
    await ui["web-audio"].play();
  } catch { toast("瀏覽器無法播放這首歌曲的音訊格式"); }
}

async function addToPlaylist(songId) {
  try {
    const items = await request("/playlist", { method:"POST", headers:{ "Content-Type":"application/json" }, body:JSON.stringify({ songId }) });
    renderPlaylist(items); toast("已加入自選播放清單");
  } catch (error) { toast(error.message); }
}

async function removeFromPlaylist(songId) {
  try {
    const items = await request(`/playlist/${encodeURIComponent(songId)}`, { method:"DELETE" });
    renderPlaylist(items); toast("已移出自選播放清單");
  } catch (error) { toast(error.message); }
}

async function loadPlaylist() {
  try { renderPlaylist(await request("/playlist")); } catch (error) { toast(error.message); }
}

function renderPlaylist(items) {
  ui.playlist.replaceChildren();
  playlistSongIds.clear();
  items.forEach(song => playlistSongIds.add(song.id));
  items.forEach(song => { songsById.set(song.id, song); ui.playlist.append(songCard(song, "playlist")); });
  ui["playlist-count"].textContent = items.length;
  ui["playlist-empty"].hidden = items.length > 0;
  document.querySelectorAll(".playlist-badge").forEach(badge => { badge.hidden = !playlistSongIds.has(badge.dataset.songId); });
}

async function loadHistory() {
  try {
    const entries = await request("/history?limit=100"); ui.history.replaceChildren();
    if (!entries.length) { ui.history.innerHTML = '<p class="empty">尚無遊玩紀錄。</p>'; return; }
    const missingIds = [...new Set(entries.map(entry => entry.songId).filter(id => !songsById.has(id)))];
    await Promise.all(missingIds.map(async id => {
      try { songsById.set(id, await request(songPath(id))); } catch { /* Song may have been removed. */ }
    }));
    entries.forEach(entry => {
      const row = document.createElement("div"); row.className = "history-item";
      row.dataset.songId = entry.songId;
      const song = songsById.get(entry.songId);
      const copy = document.createElement("div"), heading = document.createElement("div"), title = document.createElement("strong"), detail = document.createElement("small"), badges = document.createElement("div"), actions = document.createElement("div");
      copy.className = "history-copy"; heading.className = "history-heading"; badges.className = "badges"; actions.className = "history-actions";
      title.textContent = song?.title || entry.songId;
      detail.textContent = `${entry.difficulty.toUpperCase()} · ${new Date(entry.startedAtUtc).toLocaleString()}`;
      if (song?.genre) { const genre = document.createElement("span"); genre.className = "badge genre-badge"; genre.textContent = song.genre; badges.append(genre); }
      const playlistBadge = document.createElement("span"); playlistBadge.className = "badge playlist-badge"; playlistBadge.dataset.songId = entry.songId; playlistBadge.textContent = "自選播放清單"; playlistBadge.hidden = !playlistSongIds.has(entry.songId); badges.append(playlistBadge);
      heading.append(title);
      if (song) { const favorite = document.createElement("button"); favorite.type = "button"; favorite.className = "favorite icon-button history-heart"; bindFavorite(favorite, song); heading.append(favorite); }
      copy.append(heading, badges, detail);

      const webPreview = document.createElement("button"); webPreview.className = "secondary"; webPreview.textContent = "網頁試聽"; webPreview.disabled = !song?.webPreviewAvailable; webPreview.onclick = () => song && playInBrowser(song);
      const gamePreview = document.createElement("button"); gamePreview.className = "secondary"; gamePreview.textContent = "遊戲 Preview"; gamePreview.disabled = !song; gamePreview.onclick = () => command("/preview", { songId: entry.songId });
      const play = document.createElement("button"); play.textContent = "再玩一次"; play.onclick = () => command(`/history/${entry.historyId}/play`, {});
      actions.append(webPreview, gamePreview, play);
      row.append(copy, actions); ui.history.append(row);
    });
  } catch { /* History is optional while the game starts. */ }
}

document.querySelectorAll(".tab").forEach(tab => tab.addEventListener("click", () => {
  document.querySelectorAll(".tab").forEach(item => item.classList.toggle("active", item === tab));
  document.querySelectorAll(".tab-panel").forEach(panel => {
    const active = panel.id === tab.dataset.tab; panel.hidden = !active; panel.classList.toggle("active", active);
  });
  if (tab.dataset.tab === "playlist-panel") loadPlaylist();
  if (tab.dataset.tab === "history-panel") loadHistory();
}));
ui.filters.addEventListener("input", () => { clearTimeout(debounce); debounce = setTimeout(() => loadSongs(true), 220); });
ui["load-more"].onclick = () => { page += 1; loadSongs(); };

async function start() {
  try {
    await request("/health"); setOnline(true);
    await loadGenres(); await Promise.all([loadSongs(true), loadPlaylist()]); await Promise.all([refreshState(), loadHistory()]);
  } catch {
    setOnline(false); ui.error.textContent = "無法連接 OpenTaiko。請確認 RemoteControl.Enabled=1。"; ui.error.hidden = false;
  }
  const events = new EventSource(`${api}/events`);
  events.onmessage = event => {
    refreshState();
    try { const message = JSON.parse(event.data); if (message.type === "history") loadHistory(); } catch { /* ignore legacy event */ }
  };
  events.onerror = () => setOnline(false);
  setInterval(refreshState, 3000);
}
start();
