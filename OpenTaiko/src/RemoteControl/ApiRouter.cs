using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenTaiko.RemoteControl;

internal interface IRemoteControlApi {
	HealthDto GetHealth();
	GameStateDto GetState();
	SongPageDto GetSongs(SongQuery query);
	SongDto? GetSong(string songId);
	SongAudioDto? GetSongAudio(string songId);
	IReadOnlyList<string> GetGenres();
	IReadOnlyList<PlaylistDto> GetPlaylists();
	PlaylistExportDto ExportPlaylists();
	PlaylistDto CreatePlaylist(string name);
	PlaylistDto RenamePlaylist(Guid playlistId, string name);
	void DeletePlaylist(Guid playlistId);
	PlaylistDto AddSongToPlaylist(Guid playlistId, string songId);
	PlaylistDto RemoveSongFromPlaylist(Guid playlistId, string songId);
	PlaylistDto ReorderPlaylist(Guid playlistId, IReadOnlyList<string> songIds);
	void ImportPlaylists(PlaylistExportDto document);
	IReadOnlyList<PlayHistoryEntryDto> GetHistory(int limit);
	bool TryGetCommand(Guid commandId, out RemoteCommandResult? result);
	RemoteCommandResult Enqueue<TPayload>(RemoteCommandType type, TPayload payload);
}

internal sealed record ApiRequest(
	string Method,
	string Path,
	string? QueryString = null,
	string? ContentType = null,
	byte[]? Body = null);

internal sealed record ApiResponse(int StatusCode, string ContentType, byte[] Body) {
	public string BodyText => Encoding.UTF8.GetString(this.Body);
}

internal sealed class ApiRouter {
	public const int MaximumRequestBodyBytes = 64 * 1024;
	private const string ApiPrefix = "/api/v1";
	private readonly IRemoteControlApi api;

	public ApiRouter(IRemoteControlApi api) {
		this.api = api;
	}

	public ApiResponse Route(ApiRequest request) {
		try {
			string path = NormalizePath(request.Path);
			if (!path.StartsWith(ApiPrefix, StringComparison.Ordinal)) {
				return Error(404, "NOT_FOUND", "The requested endpoint does not exist.");
			}

			string relativePath = path[ApiPrefix.Length..];
			return request.Method.ToUpperInvariant() switch {
				"GET" => this.RouteGet(relativePath, ParseQuery(request.QueryString)),
				"POST" => this.RoutePost(relativePath, request),
				"PATCH" => this.RoutePatch(relativePath, request),
				"PUT" => this.RoutePut(relativePath, request),
				"DELETE" => this.RouteDelete(relativePath),
				_ => Error(405, "METHOD_NOT_ALLOWED", "The HTTP method is not supported for this endpoint."),
			};
		} catch (JsonException) {
			return Error(400, ApiErrorCodes.InvalidRequest, "The request body is not valid JSON.");
		} catch (ArgumentException exception) {
			return Error(400, ApiErrorCodes.InvalidRequest, exception.Message);
		} catch (ApiRouteException exception) {
			return Error(exception.StatusCode, exception.ErrorCode, exception.Message);
		} catch (Exception) {
			return Error(500, ApiErrorCodes.InternalError, "An unexpected server error occurred.");
		}
	}

	private ApiResponse RouteGet(string path, IReadOnlyDictionary<string, string> query) {
		if (path == "/health") return Json(200, this.api.GetHealth());
		if (path == "/state") return Json(200, this.api.GetState());
		if (path == "/genres") return Json(200, this.api.GetGenres());
		if (path == "/playlists/export") return Json(200, this.api.ExportPlaylists());
		if (path == "/playlists") return Json(200, this.api.GetPlaylists());
		if (path == "/songs") {
			if (!TryOptionalDifficulty(query, out ApiDifficulty? difficulty)) {
				return Error(422, ApiErrorCodes.InvalidDifficulty, "Unknown difficulty value.");
			}
			SongQuery songQuery = new(
				GetOptional(query, "query"),
				GetOptional(query, "genre"),
				difficulty,
				ParseOptionalInt(query, "minLevel", 1, 99),
				ParseOptionalInt(query, "maxLevel", 1, 99),
				ParseInt(query, "page", 1, int.MaxValue, 1),
				ParseInt(query, "pageSize", 1, 500, 100),
				ParseOptionalBool(query, "favorite"));
			if (songQuery.MinimumLevel > songQuery.MaximumLevel) {
				return Error(400, ApiErrorCodes.InvalidRequest, "minLevel cannot exceed maxLevel.");
			}
			return Json(200, this.api.GetSongs(songQuery));
		}
		if (path == "/history") {
			int limit = ParseInt(query, "limit", 1, 100, 100);
			return Json(200, this.api.GetHistory(limit));
		}
		if (TryNestedSongPath(path, "/audio", out string audioSongId)) {
			SongAudioDto? audio = this.api.GetSongAudio(audioSongId);
			return audio is null
				? Error(404, "PREVIEW_NOT_FOUND", "No browser-compatible preview audio is available for this song.")
				: new ApiResponse(200, audio.ContentType, audio.Content);
		}
		if (TryPathValue(path, "/songs/", out string? songId)) {
			SongDto? song = this.api.GetSong(songId);
			return song is null
				? Error(404, ApiErrorCodes.SongNotFound, "The requested song was not found.")
				: Json(200, song);
		}
		if (TryPathValue(path, "/commands/", out string? commandIdText)) {
			if (!Guid.TryParse(commandIdText, out Guid commandId)) {
				return Error(400, ApiErrorCodes.InvalidRequest, "commandId must be a UUID.");
			}
			return this.api.TryGetCommand(commandId, out RemoteCommandResult? result)
				? Json(200, result)
				: Error(404, "COMMAND_NOT_FOUND", "The requested command was not found.");
		}

		return Error(404, "NOT_FOUND", "The requested endpoint does not exist.");
	}

	private ApiResponse RoutePost(string path, ApiRequest request) {
		if (request.Body?.Length > MaximumRequestBodyBytes) {
			return Error(413, "REQUEST_TOO_LARGE", "The request body exceeds 65536 bytes.");
		}
		bool bodyRequired = path is "/selection" or "/play" or "/preview" or "/favorite" or "/playlists"
			|| path.EndsWith("/songs", StringComparison.Ordinal);
		if ((bodyRequired || request.Body?.Length > 0) && !IsJsonContentType(request.ContentType)) {
			return Error(415, "UNSUPPORTED_MEDIA_TYPE", "POST requests require application/json.");
		}

		if (path == "/selection") {
			SelectionRequest body = DeserializeRequired<SelectionRequest>(request.Body);
			ValidateSongId(body.SongId);
			return this.Enqueue(RemoteCommandType.Select, body);
		}
		if (path == "/play") {
			PlayRequest body = DeserializeRequired<PlayRequest>(request.Body);
			ValidateSongId(body.SongId);
			if (body.PlayerCount is < 1 or > 5) {
				return Error(400, ApiErrorCodes.InvalidRequest, "playerCount must be between 1 and 5.");
			}
			return this.Enqueue(RemoteCommandType.Play, body);
		}
		if (path == "/preview") {
			PreviewRequest body = DeserializeRequired<PreviewRequest>(request.Body);
			ValidateSongId(body.SongId);
			return this.Enqueue(RemoteCommandType.Preview, body);
		}
		if (path == "/favorite") {
			FavoriteRequest body = DeserializeRequired<FavoriteRequest>(request.Body);
			ValidateSongId(body.SongId);
			return this.Enqueue(RemoteCommandType.SetFavorite, body);
		}
		if (path == "/playlists") {
			PlaylistCreateRequest body = DeserializeRequired<PlaylistCreateRequest>(request.Body);
			return Json(201, this.api.CreatePlaylist(body.Name));
		}
		if (TryPlaylistPath(path, "/songs", out Guid playlistId)) {
			PlaylistSongRequest body = DeserializeRequired<PlaylistSongRequest>(request.Body);
			ValidateSongId(body.SongId);
			return Json(200, this.api.AddSongToPlaylist(playlistId, body.SongId));
		}
		if (path == "/preview/stop") {
			EnsureEmptyJsonObject(request.Body);
			return this.Enqueue(RemoteCommandType.StopPreview, new { });
		}
		if (path == "/restart") {
			RestartRequest body = DeserializeOptional(request.Body, new RestartRequest(null));
			return this.Enqueue(RemoteCommandType.Restart, body);
		}
		if (path == "/gameplay/exit") {
			EnsureEmptyJsonObject(request.Body);
			return this.Enqueue(RemoteCommandType.ExitGameplay, new { });
		}
		if (path == "/gameplay/retry") {
			EnsureEmptyJsonObject(request.Body);
			return this.Enqueue(RemoteCommandType.RetryGameplay, new { });
		}
		if (path == "/results/exit") {
			EnsureEmptyJsonObject(request.Body);
			return this.Enqueue(RemoteCommandType.ExitResults, new { });
		}
		if (path.StartsWith("/history/", StringComparison.Ordinal) && path.EndsWith("/play", StringComparison.Ordinal)) {
			string idText = path["/history/".Length..^"/play".Length];
			if (!Guid.TryParse(Uri.UnescapeDataString(idText), out Guid historyId)) {
				return Error(400, ApiErrorCodes.InvalidRequest, "historyId must be a UUID.");
			}
			EnsureEmptyJsonObject(request.Body);
			return this.Enqueue(RemoteCommandType.Restart, new RestartRequest(historyId));
		}

		return Error(404, "NOT_FOUND", "The requested endpoint does not exist.");
	}

	private ApiResponse RoutePatch(string path, ApiRequest request) {
		RequireJson(request);
		if (TryPlaylistPath(path, string.Empty, out Guid playlistId)) {
			PlaylistRenameRequest body = DeserializeRequired<PlaylistRenameRequest>(request.Body);
			return Json(200, this.api.RenamePlaylist(playlistId, body.Name));
		}
		return Error(404, "NOT_FOUND", "The requested endpoint does not exist.");
	}

	private ApiResponse RoutePut(string path, ApiRequest request) {
		RequireJson(request);
		if (path == "/playlists/import") {
			PlaylistExportDto body = DeserializeRequired<PlaylistExportDto>(request.Body);
			this.api.ImportPlaylists(body);
			return Json(200, this.api.GetPlaylists());
		}
		if (TryPlaylistPath(path, "/songs", out Guid playlistId)) {
			PlaylistReorderRequest body = DeserializeRequired<PlaylistReorderRequest>(request.Body);
			return Json(200, this.api.ReorderPlaylist(playlistId, body.SongIds));
		}
		return Error(404, "NOT_FOUND", "The requested endpoint does not exist.");
	}

	private ApiResponse RouteDelete(string path) {
		if (TryPlaylistSongPath(path, out Guid playlistId, out string songId)) {
			return Json(200, this.api.RemoveSongFromPlaylist(playlistId, songId));
		}
		if (TryPlaylistPath(path, string.Empty, out playlistId)) {
			this.api.DeletePlaylist(playlistId);
			return Json(200, this.api.GetPlaylists());
		}
		return Error(405, "METHOD_NOT_ALLOWED", "The HTTP method is not supported for this endpoint.");
	}

	private ApiResponse Enqueue<TPayload>(RemoteCommandType type, TPayload payload) {
		RemoteCommandResult result = this.api.Enqueue(type, payload);
		return Json(202, new CommandAcceptedDto(result.CommandId, result.Status));
	}

	private static T DeserializeRequired<T>(byte[]? body) {
		if (body is null || body.Length == 0) throw new ArgumentException("A JSON request body is required.");
		T? value = JsonSerializer.Deserialize<T>(body, RemoteControlJson.Options);
		return value ?? throw new ArgumentException("A JSON request body is required.");
	}

	private static T DeserializeOptional<T>(byte[]? body, T fallback) {
		if (body is null || body.Length == 0) return fallback;
		return JsonSerializer.Deserialize<T>(body, RemoteControlJson.Options) ?? fallback;
	}

	private static void EnsureEmptyJsonObject(byte[]? body) {
		if (body is null || body.Length == 0) return;
		using JsonDocument document = JsonDocument.Parse(body);
		if (document.RootElement.ValueKind != JsonValueKind.Object
			|| document.RootElement.EnumerateObject().Any()) {
			throw new ArgumentException("This endpoint only accepts an empty JSON object.");
		}
	}

	private static void ValidateSongId(string? songId) {
		if (string.IsNullOrWhiteSpace(songId)) {
			throw new ArgumentException("songId is required.");
		}
	}

	private static void RequireJson(ApiRequest request) {
		if (request.Body?.Length > MaximumRequestBodyBytes) throw new ApiRouteException(413, "REQUEST_TOO_LARGE", "The request body exceeds 65536 bytes.");
		if (!IsJsonContentType(request.ContentType)) throw new ApiRouteException(415, "UNSUPPORTED_MEDIA_TYPE", "This request requires application/json.");
	}

	private static bool IsJsonContentType(string? contentType)
		=> contentType?.Split(';', 2)[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) == true;

	private static string NormalizePath(string path) {
		if (string.IsNullOrWhiteSpace(path)) return "/";
		string normalized = path.StartsWith('/') ? path : "/" + path;
		return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
	}

	private static bool TryPathValue(string path, string prefix, out string value) {
		value = string.Empty;
		if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) return false;
		string raw = path[prefix.Length..];
		if (raw.Contains('/')) return false;
		value = Uri.UnescapeDataString(raw);
		return true;
	}

	private static bool TryNestedSongPath(string path, string suffix, out string songId) {
		songId = string.Empty;
		const string prefix = "/songs/";
		if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return false;
		string raw = path[prefix.Length..^suffix.Length];
		if (string.IsNullOrWhiteSpace(raw) || raw.Contains('/')) return false;
		songId = Uri.UnescapeDataString(raw);
		return true;
	}

	private static bool TryPlaylistPath(string path, string suffix, out Guid playlistId) {
		playlistId = Guid.Empty;
		const string prefix = "/playlists/";
		if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return false;
		int end = suffix.Length == 0 ? path.Length : path.Length - suffix.Length;
		string idText = path[prefix.Length..end];
		return !idText.Contains('/') && Guid.TryParse(Uri.UnescapeDataString(idText), out playlistId);
	}

	private static bool TryPlaylistSongPath(string path, out Guid playlistId, out string songId) {
		playlistId = Guid.Empty;
		songId = string.Empty;
		string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length != 4 || segments[0] != "playlists" || segments[2] != "songs"
			|| !Guid.TryParse(segments[1], out playlistId)) return false;
		songId = Uri.UnescapeDataString(segments[3]);
		return !string.IsNullOrWhiteSpace(songId);
	}

	private static Dictionary<string, string> ParseQuery(string? queryString) {
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(queryString)) return result;
		foreach (string pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			string[] parts = pair.Split('=', 2);
			result[WebUtility.UrlDecode(parts[0])] = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
		}
		return result;
	}

	private static string? GetOptional(IReadOnlyDictionary<string, string> query, string key)
		=> query.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

	private static int ParseInt(IReadOnlyDictionary<string, string> query, string key, int minimum, int maximum, int fallback) {
		if (!query.TryGetValue(key, out string? text)) return fallback;
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum) {
			throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
		}
		return value;
	}

	private static int? ParseOptionalInt(IReadOnlyDictionary<string, string> query, string key, int minimum, int maximum)
		=> query.ContainsKey(key) ? ParseInt(query, key, minimum, maximum, minimum) : null;

	private static bool? ParseOptionalBool(IReadOnlyDictionary<string, string> query, string key) {
		if (!query.TryGetValue(key, out string? value)) return null;
		if (bool.TryParse(value, out bool parsed)) return parsed;
		throw new ArgumentException($"{key} must be true or false.");
	}

	private static bool TryOptionalDifficulty(IReadOnlyDictionary<string, string> query, out ApiDifficulty? difficulty) {
		difficulty = null;
		if (!query.TryGetValue("difficulty", out string? value)) return true;
		if (!Enum.TryParse(value, true, out ApiDifficulty parsed)) return false;
		difficulty = parsed;
		return true;
	}

	private static ApiResponse Json<T>(int statusCode, T value)
		=> new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, RemoteControlJson.Options));

	private static ApiResponse Error(int statusCode, string code, string message)
		=> Json(statusCode, new ApiErrorBody(new ApiError(code, message, new Dictionary<string, JsonElement>())));
}

internal sealed class ApiRouteException : Exception {
	public ApiRouteException(int statusCode, string errorCode, string message) : base(message) {
		this.StatusCode = statusCode;
		this.ErrorCode = errorCode;
	}

	public int StatusCode { get; }
	public string ErrorCode { get; }
}
