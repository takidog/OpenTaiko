using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTaiko.RemoteControl;

internal enum ApiDifficulty {
	Easy,
	Normal,
	Hard,
	Oni,
	Ura,
	Tower,
	Dan,
}

internal sealed record SongDifficultyDto(
	[property: JsonPropertyName("id")] ApiDifficulty Id,
	[property: JsonPropertyName("level")] int Level,
	[property: JsonPropertyName("available")] bool Available);

internal sealed record SongDto(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("title")] string Title,
	[property: JsonPropertyName("titles")] IReadOnlyDictionary<string, string> Titles,
	[property: JsonPropertyName("subtitle")] string Subtitle,
	[property: JsonPropertyName("genre")] string Genre,
	[property: JsonPropertyName("maker")] string Maker,
	[property: JsonPropertyName("breadcrumb")] string Breadcrumb,
	[property: JsonPropertyName("difficulties")] IReadOnlyList<SongDifficultyDto> Difficulties,
	[property: JsonPropertyName("favorite")] bool Favorite);

internal sealed record SelectionRequest(
	[property: JsonPropertyName("songId")] string SongId,
	[property: JsonPropertyName("difficulty")] ApiDifficulty Difficulty);

internal sealed record PlayRequest(
	[property: JsonPropertyName("songId")] string SongId,
	[property: JsonPropertyName("difficulty")] ApiDifficulty Difficulty,
	[property: JsonPropertyName("playerCount")] int? PlayerCount);

internal sealed record PreviewRequest(
	[property: JsonPropertyName("songId")] string SongId);

internal sealed record RestartRequest(
	[property: JsonPropertyName("historyId")] Guid? HistoryId);

internal sealed record ApiErrorBody(
	[property: JsonPropertyName("error")] ApiError Error);

internal sealed record ApiError(
	[property: JsonPropertyName("code")] string Code,
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("details")] IReadOnlyDictionary<string, JsonElement> Details);

internal static class RemoteControlJson {
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
		PropertyNameCaseInsensitive = false,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};
}

internal static class ApiErrorCodes {
	public const string InvalidRequest = "INVALID_REQUEST";
	public const string InvalidDifficulty = "INVALID_DIFFICULTY";
	public const string SongNotFound = "SONG_NOT_FOUND";
	public const string HistoryNotFound = "HISTORY_NOT_FOUND";
	public const string GameBusy = "GAME_BUSY";
	public const string InternalError = "INTERNAL_ERROR";
}
