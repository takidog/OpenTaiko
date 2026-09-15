const api = "/api/v1";
const ui = Object.fromEntries(["connection","stage","current-song","players","stop-preview","song-count","query","genre","difficulty","min-level","max-level","songs","history","error","load-more","filters","toast"].map(id => [id, document.getElementById(id)]));
let page = 1, total = 0, loading = false, debounce, songsById = new Map();

async function request(path, options) {
  const response = await fetch(api + path, options);
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error?.message || `HTTP ${response.status}`);
  return body;
}

function toast(message) { ui.toast.textContent = message; ui.toast.classList.add("show"); setTimeout(() => ui.toast.classList.remove("show"), 2200); }
function setOnline(online) { ui.connection.textContent = online ? "已連線" : "OpenTaiko 離線"; ui.connection.className = `pill ${online ? "online" : "offline"}`; }

async function refreshState() {
  try {
    const state = await request("/state");
    setOnline(true); ui.stage.textContent = state.stage; ui.players.textContent = `${state.playerCount}P`;
    ui["current-song"].textContent = songsById.get(state.songId)?.title || state.songId || "尚未選擇";
  } catch { setOnline(false); }
}

function filters() {
  const params = new URLSearchParams({ page, pageSize: 40 });
  if (ui.query.value.trim()) params.set("query", ui.query.value.trim());
  if (ui.difficulty.value) params.set("difficulty", ui.difficulty.value);
  if (ui.genre.value.trim()) params.set("genre", ui.genre.value.trim());
  if (ui["min-level"].value) params.set("minLevel", ui["min-level"].value);
  if (ui["max-level"].value) params.set("maxLevel", ui["max-level"].value);
  return params;
}

async function loadSongs(reset = false) {
  if (loading) return; loading = true;
  if (reset) { page = 1; ui.songs.replaceChildren(); songsById.clear(); }
  ui.error.hidden = true;
  try {
    const result = await request(`/songs?${filters()}`); total = result.total;
    result.items.forEach(song => { songsById.set(song.id, song); ui.songs.append(songCard(song)); });
    ui["song-count"].textContent = `${total} 首`; ui["load-more"].hidden = page * result.pageSize >= total;
  } catch (error) { ui.error.textContent = error.message; ui.error.hidden = false; }
  finally { loading = false; }
}

function songCard(song) {
  const card = document.getElementById("song-template").content.firstElementChild.cloneNode(true);
  card.querySelector("h3").textContent = song.title; card.querySelector(".genre").textContent = song.genre || "OTHER";
  card.querySelector(".meta").textContent = [song.subtitle, song.maker].filter(Boolean).join(" · ");
  let selected = song.difficulties.find(d => d.id === ui.difficulty.value) || song.difficulties.find(d => d.id === "oni") || song.difficulties[0];
  const holder = card.querySelector(".difficulties");
  song.difficulties.filter(d => d.available).forEach(diff => {
    const button = document.createElement("button"); button.type = "button"; button.className = "diff"; button.textContent = `${diff.id.toUpperCase()} ★${diff.level}`;
    if (diff === selected) button.classList.add("selected");
    button.addEventListener("click", () => { selected = diff; holder.querySelectorAll(".diff").forEach(x => x.classList.remove("selected")); button.classList.add("selected"); });
    holder.append(button);
  });
  card.querySelector(".preview").onclick = () => command("/preview", { songId: song.id });
  card.querySelector(".select").onclick = () => command("/selection", { songId: song.id, difficulty: selected.id });
  card.querySelector(".play").onclick = () => command("/play", { songId: song.id, difficulty: selected.id });
  return card;
}

async function command(path, payload) {
  try { const result = await request(path, { method:"POST", headers:{ "Content-Type":"application/json" }, body:JSON.stringify(payload) }); toast(`指令已送出 · ${result.commandId.slice(0,8)}`); }
  catch (error) { toast(error.message); }
}

async function loadHistory() {
  try {
    const entries = await request("/history?limit=100"); ui.history.replaceChildren();
    entries.forEach(entry => {
      const row = document.createElement("div"); row.className = "history-item";
      const copy = document.createElement("div"), title = document.createElement("strong"), detail = document.createElement("small"), play = document.createElement("button");
      title.textContent = songsById.get(entry.songId)?.title || entry.songId; detail.textContent = `${entry.difficulty.toUpperCase()} · ${new Date(entry.startedAtUtc).toLocaleString()}`;
      copy.append(title, detail); play.textContent = "再玩一次"; play.onclick = () => command(`/history/${entry.historyId}/play`, {}); row.append(copy, play); ui.history.append(row);
    });
  } catch { /* history is optional while the game starts */ }
}

ui.filters.addEventListener("input", () => { clearTimeout(debounce); debounce = setTimeout(() => loadSongs(true), 220); });
ui["load-more"].onclick = () => { page += 1; loadSongs(); };
ui["stop-preview"].onclick = () => command("/preview/stop", {});

async function start() {
  try { await request("/health"); setOnline(true); await loadSongs(true); await Promise.all([refreshState(), loadHistory()]); }
  catch { setOnline(false); ui.error.textContent = "無法連接 OpenTaiko。請確認 RemoteControl.Enabled=1。"; ui.error.hidden = false; }
  const events = new EventSource(`${api}/events`); events.onmessage = () => { refreshState(); loadHistory(); }; events.onerror = () => setOnline(false);
  setInterval(refreshState, 3000);
}
start();
