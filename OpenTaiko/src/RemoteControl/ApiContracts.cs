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

internal static class ApiDifficultyMapper {
	public static ApiDifficulty FromGameDifficulty(Difficulty difficulty) => difficulty switch {
		Difficulty.Easy => ApiDifficulty.Easy,
		Difficulty.Normal => ApiDifficulty.Normal,
		Difficulty.Hard => ApiDifficulty.Hard,
		Difficulty.Oni => ApiDifficulty.Oni,
		Difficulty.Edit => ApiDifficulty.Ura,
		Difficulty.Tower => ApiDifficulty.Tower,
		Difficulty.Dan => ApiDifficulty.Dan,
		_ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
	};

	public static Difficulty ToGameDifficulty(ApiDifficulty difficulty) => difficulty switch {
		ApiDifficulty.Easy => Difficulty.Easy,
		ApiDifficulty.Normal => Difficulty.Normal,
		ApiDifficulty.Hard => Difficulty.Hard,
		ApiDifficulty.Oni => Difficulty.Oni,
		ApiDifficulty.Ura => Difficulty.Edit,
		ApiDifficulty.Tower => Difficulty.Tower,
		ApiDifficulty.Dan => Difficulty.Dan,
		_ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
	};
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

internal sealed record HealthDto(
	[property: JsonPropertyName("status")] string Status,
	[property: JsonPropertyName("version")] string Version,
	[property: JsonPropertyName("songIndexReady")] bool SongIndexReady);

internal sealed record GameStateDto(
	[property: JsonPropertyName("stage")] string Stage,
	[property: JsonPropertyName("songId")] string? SongId,
	[property: JsonPropertyName("difficulty")] ApiDifficulty? Difficulty,
	[property: JsonPropertyName("playerCount")] int PlayerCount);

internal sealed record SongPageDto(
	[property: JsonPropertyName("items")] IReadOnlyList<SongDto> Items,
	[property: JsonPropertyName("page")] int Page,
	[property: JsonPropertyName("pageSize")] int PageSize,
	[property: JsonPropertyName("total")] int Total);

internal sealed record PlayHistoryEntryDto(
	[property: JsonPropertyName("historyId")] Guid HistoryId,
	[property: JsonPropertyName("songId")] string SongId,
	[property: JsonPropertyName("difficulty")] ApiDifficulty Difficulty,
	[property: JsonPropertyName("saveSlot")] int SaveSlot,
	[property: JsonPropertyName("playerCount")] int PlayerCount,
	[property: JsonPropertyName("playerSide")] string PlayerSide,
	[property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc,
	[property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
	[property: JsonPropertyName("status")] string Status,
	[property: JsonPropertyName("modifiers")] IReadOnlyDictionary<string, JsonElement> Modifiers);

internal sealed record SongQuery(
	string? Query,
	string? Genre,
	ApiDifficulty? Difficulty,
	int? MinimumLevel,
	int? MaximumLevel,
	int Page,
	int PageSize);

internal sealed record CommandAcceptedDto(
	[property: JsonPropertyName("commandId")] Guid CommandId,
	[property: JsonPropertyName("status")] RemoteCommandStatus Status);

internal sealed record ApiErrorBody(
	[property: JsonPropertyName("error")] ApiError Error);

internal sealed record ApiError(
	[property: JsonPropertyName("code")] string Code,
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("details")] IReadOnlyDictionary<string, JsonElement> Details);

internal static class RemoteControlJson {
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
		PropertyNameCaseInsensitive = false,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
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
