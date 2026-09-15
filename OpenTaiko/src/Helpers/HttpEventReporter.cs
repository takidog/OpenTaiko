using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using OpenTaiko.RemoteControl;

namespace OpenTaiko;

/// <summary>
/// Reports game events over HTTP in real time.
/// </summary>
internal class HttpEventReporter(
    string host,
    int port,
    ApiRouter? apiRouter = null,
    StaticFileHandler? staticFileHandler = null) {
    public string host { get; private set; } = host;
    public int port { get; private set; } = port;

    public bool started { get; private set; } = false;

    private HttpListener? _listener;
    private readonly List<EventClient> _clients = new();
    private readonly object _lockObj = new();
    private Dictionary<int, Dictionary<int, int>> _noteOrdinalMappingByPlayer = new();

    public void StartListening() {
        if (this.started) return;
        try {
            this._listener = new HttpListener();
            this._listener.Prefixes.Add($"http://{this.host}:{this.port}/");
            this._listener.Start();
            this.started = true;
            Trace.TraceInformation($"[HttpEventReporter] Listening on http://{this.host}:{this.port}/");
            _ = Task.Run(this.AcceptConnectionsAsync);
        } catch (Exception ex) {
            this._listener?.Close();
            this._listener = null;
            this.started = false;
            Trace.TraceError($"[HttpEventReporter] Listener error: {ex.Message}");
        }
    }

    private async Task AcceptConnectionsAsync() {
        try {
            while (this._listener?.IsListening == true) {
                HttpListenerContext context = await this._listener.GetContextAsync();
                _ = this.HandleClientAsync(context);
            }
        } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) {
            if (this.started) Trace.TraceError($"[HttpEventReporter] Listener error: {ex.Message}");
        }
    }

    public void StopListening() {
        if (!this.started) return;
        this.started = false;

        try {
            this._listener?.Stop();
            this._listener?.Close();
        } catch (Exception ex) {
            Trace.TraceError($"[HttpEventReporter] Stop error: {ex.Message}");
        } finally {
            this._listener = null;
            lock (this._lockObj) {
                foreach (var client in this._clients) {
                    try { client.Response.Close(); } catch { }
                }
                this._clients.Clear();
            }
            Trace.TraceInformation("[HttpEventReporter] Stopped listening.");
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context) {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        bool eventStream = (path == "/api/v1/events" && apiRouter is not null)
            || (path == "/" && (staticFileHandler is null || context.Request.AcceptTypes?.Contains("text/event-stream") == true));
        if (eventStream) {
            this.HandleEventStream(context, path == "/");
            return;
        }
        if (!path.StartsWith("/api/", StringComparison.Ordinal) && staticFileHandler is not null) {
            await WriteResponseAsync(context.Response, staticFileHandler.Route(context.Request.HttpMethod, path));
            return;
        }
        if (path != "/") {
            await this.HandleApiRequestAsync(context, path);
            return;
        }

		context.Response.StatusCode = 404;
		context.Response.Close();
    }

	private void HandleEventStream(HttpListenerContext context, bool legacyGameEvents) {
        HttpListenerResponse response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        response.Headers.Add("Access-Control-Allow-Origin", "*");

        try {
            byte[] init = Encoding.UTF8.GetBytes(": connected\n\n");
            response.OutputStream.Write(init, 0, init.Length);
            response.OutputStream.Flush();
        } catch (Exception ex) {
            Trace.TraceError($"[HttpEventReporter] Failed to send init to client: {ex.Message}");
            response.Close();
            return;
        }

        lock (this._lockObj) {
            this._clients.Add(new EventClient(response, legacyGameEvents));
        }
        Trace.TraceInformation("[HttpEventReporter] Client connected.");
    }

    private async Task HandleApiRequestAsync(HttpListenerContext context, string path) {
        if (apiRouter is null) {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        byte[] body = await ReadRequestBodyAsync(context.Request);
        ApiResponse apiResponse = apiRouter.Route(new ApiRequest(
            context.Request.HttpMethod,
            path,
            context.Request.Url?.Query,
            context.Request.ContentType,
            body));
        await WriteResponseAsync(context.Response, apiResponse);
    }

	private static async Task WriteResponseAsync(HttpListenerResponse response, ApiResponse apiResponse) {
		response.StatusCode = apiResponse.StatusCode;
		response.ContentType = apiResponse.ContentType;
		response.ContentEncoding = Encoding.UTF8;
		response.ContentLength64 = apiResponse.Body.Length;
		try {
			await response.OutputStream.WriteAsync(apiResponse.Body);
		} finally {
			response.Close();
		}
	}

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request) {
        if (!request.HasEntityBody) return Array.Empty<byte>();
        if (request.ContentLength64 > ApiRouter.MaximumRequestBodyBytes) {
            return new byte[ApiRouter.MaximumRequestBodyBytes + 1];
        }

        using MemoryStream body = new();
        byte[] buffer = new byte[8192];
        int remaining = ApiRouter.MaximumRequestBodyBytes + 1;
        while (remaining > 0) {
            int read = await request.InputStream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
            if (read == 0) break;
            body.Write(buffer, 0, read);
            remaining -= read;
        }
        return body.ToArray();
    }

    public void ReportNoteJudgement(ENoteJudge noteJudge, int player, CChip? chip, int? msDelta) {
        if (chip == null) return;
        NotesManager.ENoteType noteType = NotesManager.GetNoteType(chip);
        Dictionary<int, int> mappingForPlayer =
            this._noteOrdinalMappingByPlayer.GetValueOrDefault(player, new());
        int? noteOrdinalByChar = mappingForPlayer.ContainsKey(chip.n整数値_内部番号)
            ? mappingForPlayer[chip.n整数値_内部番号]
            : null;
        if (!(NotesManager.IsHittableNote(chip) && !NotesManager.IsGenericRoll(chip))) { return; }
        this.Broadcast(new {
            type = "judgement",
            judgement = StringForSerailization(noteJudge),
            msDelta,
            noteChar = NotesManager.ToNoteChar(noteType),
            noteOrdinalByChar
        }, legacyOnly: true);
    }

    // Explicitly state known cases so that the event format is stable against future enum changes.
	static string StringForSerailization(ENoteJudge noteJudge) {
		return noteJudge switch {
			ENoteJudge.Perfect => "perfect",
			ENoteJudge.Great => "great",
			ENoteJudge.Good => "good",
			ENoteJudge.Poor => "poor",
			ENoteJudge.Miss => "miss",
			ENoteJudge.Bad => "bad",
			ENoteJudge.Auto => "auto",
			ENoteJudge.ADLIB => "adlib",
			ENoteJudge.Mine => "mine",
			_ => noteJudge.ToString(),
		};
	}

    public void ReportGameplayStart() {
        this._noteOrdinalMappingByPlayer = new();
        var tjaSummaries = Enumerable.Range(0, OpenTaiko.ConfigIni.nPlayerCount).Select(i => {
			CTja? tja = OpenTaiko.GetTJA(i);
            if (tja is null) return null;
            this.BuildNoteOrdinalMapping(i, tja);
            int difficultyInt = OpenTaiko.SongMount.nChoosenSongDifficulty[i];
            if (!Enum.IsDefined(typeof(Difficulty), difficultyInt)) return null;
            string difficulty = ((Difficulty)difficultyInt).ToString();
			string fullPath = tja.strFullPath;
            string tjaContent = File.ReadAllText(fullPath);
            return new {
                player = i,
                tjaContent,
                difficulty
            };
        }).Where(n => n is not null);

        this.Broadcast(new {
            type = "gameplay_start",
			tjaSummaries
		}, legacyOnly: true);
    }

	public void ReportRemoteControlEvent(string type, object data) {
		this.Broadcast(new { type, data });
	}

    private void BuildNoteOrdinalMapping(int player, CTja tja) {
        Dictionary<int, int> mappingForPlayer = new();
        this._noteOrdinalMappingByPlayer[player] = mappingForPlayer;
        Dictionary<NotesManager.ENoteType, int> numNotesSeenByType = new();
        foreach (CChip chip in tja.listNoteChip) {
            NotesManager.ENoteType noteType = NotesManager.GetNoteType(chip);
            numNotesSeenByType.TryGetValue(noteType, out int numNotes);
            mappingForPlayer[chip.n整数値_内部番号] = numNotes;
            numNotesSeenByType[noteType] = numNotes + 1;
        }
        List<CChip> noteChips = tja.listNoteChip;
    }

    private void Broadcast(object data, bool legacyOnly = false) {
        try {
            string json = JsonSerializer.Serialize(data, RemoteControlJson.Options);
            string eventString = $"data: {json}\n\n";
            byte[] buffer = Encoding.UTF8.GetBytes(eventString);

            lock (this._lockObj) {
                for (int i = this._clients.Count - 1; i >= 0; i--) {
                    if (legacyOnly && !this._clients[i].LegacyGameEvents) continue;
                    try {
                        this._clients[i].Response.OutputStream.Write(buffer, 0, buffer.Length);
                        this._clients[i].Response.OutputStream.Flush();
                    } catch {
                        try { this._clients[i].Response.Close(); } catch { }
                        this._clients.RemoveAt(i);
                    }
                }
            }
        } catch (Exception ex) {
            Trace.TraceError($"[HttpEventReporter] Broadcast error: {ex.Message}");
        }
    }

	private sealed record EventClient(HttpListenerResponse Response, bool LegacyGameEvents);
}
