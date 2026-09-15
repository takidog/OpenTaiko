using System.Text;
using System.Text.Json;
using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class ApiRouterTests {
	private readonly FakeApi api = new();

	[Fact]
	public void HealthAndStateReturnUtf8Json() {
		ApiRouter router = new(this.api);

		ApiResponse health = router.Route(new ApiRequest("GET", "/api/v1/health"));
		ApiResponse state = router.Route(new ApiRequest("GET", "/api/v1/state"));

		Assert.Equal(200, health.StatusCode);
		Assert.Equal("application/json; charset=utf-8", health.ContentType);
		Assert.Contains("\"songIndexReady\":true", health.BodyText);
		Assert.Equal(200, state.StatusCode);
		Assert.Contains("\"difficulty\":\"ura\"", state.BodyText);
	}

	[Fact]
	public void SongsParsesFiltersPaginationAndEncodedIds() {
		ApiRouter router = new(this.api);

		ApiResponse list = router.Route(new ApiRequest(
			"GET",
			"/api/v1/songs",
			"?query=%E5%A4%AA%E9%BC%93&genre=Anime&difficulty=ura&minLevel=8&maxLevel=10&page=2&pageSize=25"));
		ApiResponse item = router.Route(new ApiRequest("GET", "/api/v1/songs/song%2Bone"));

		Assert.Equal(200, list.StatusCode);
		Assert.Equal("太鼓", this.api.LastSongQuery!.Query);
		Assert.Equal(ApiDifficulty.Ura, this.api.LastSongQuery.Difficulty);
		Assert.Equal(2, this.api.LastSongQuery.Page);
		Assert.Equal(25, this.api.LastSongQuery.PageSize);
		Assert.Equal(200, item.StatusCode);
		Assert.Equal("song+one", this.api.LastSongId);
	}

	[Theory]
	[InlineData("?difficulty=impossible", 422, "INVALID_DIFFICULTY")]
	[InlineData("?pageSize=501", 400, "INVALID_REQUEST")]
	[InlineData("?minLevel=10&maxLevel=2", 400, "INVALID_REQUEST")]
	public void SongsRejectsInvalidQueries(string query, int status, string code) {
		ApiResponse response = new ApiRouter(this.api).Route(new ApiRequest("GET", "/api/v1/songs", query));

		AssertError(response, status, code);
	}

	[Fact]
	public void MissingResourcesUseStableErrors() {
		ApiRouter router = new(this.api);

		AssertError(router.Route(new ApiRequest("GET", "/api/v1/songs/missing")), 404, ApiErrorCodes.SongNotFound);
		AssertError(router.Route(new ApiRequest("GET", $"/api/v1/commands/{Guid.NewGuid()}")), 404, "COMMAND_NOT_FOUND");
		AssertError(router.Route(new ApiRequest("GET", "/not-api")), 404, "NOT_FOUND");
	}

	[Fact]
	public void PostQueuesCommandsAndReturnsCommandId() {
		ApiRouter router = new(this.api);
		ApiResponse response = router.Route(JsonPost("/api/v1/play", """{"songId":"song+one","difficulty":"ura","playerCount":2}"""));

		Assert.Equal(202, response.StatusCode);
		Assert.Equal(RemoteCommandType.Play, this.api.LastCommandType);
		PlayRequest payload = Assert.IsType<PlayRequest>(this.api.LastPayload);
		Assert.Equal(ApiDifficulty.Ura, payload.Difficulty);
		Assert.Equal(2, payload.PlayerCount);
		using JsonDocument json = JsonDocument.Parse(response.Body);
		Assert.True(json.RootElement.GetProperty("commandId").GetGuid() != Guid.Empty);
		Assert.Equal("pending", json.RootElement.GetProperty("status").GetString());
	}

	[Theory]
	[InlineData("text/plain", "{}", 415, "UNSUPPORTED_MEDIA_TYPE")]
	[InlineData("application/json", "{", 400, "INVALID_REQUEST")]
	[InlineData("application/json", "{\"songId\":\"x\",\"difficulty\":7}", 400, "INVALID_REQUEST")]
	[InlineData("application/json", "{\"songId\":\"\",\"difficulty\":\"oni\"}", 400, "INVALID_REQUEST")]
	public void PostValidatesContentAndJson(string contentType, string body, int status, string code) {
		ApiRequest request = new("POST", "/api/v1/play", ContentType: contentType, Body: Encoding.UTF8.GetBytes(body));
		AssertError(new ApiRouter(this.api).Route(request), status, code);
	}

	[Fact]
	public void PostRejectsOversizedBodiesAndUnsupportedMethods() {
		ApiRouter router = new(this.api);

		AssertError(router.Route(new ApiRequest(
			"POST",
			"/api/v1/play",
			ContentType: "application/json",
			Body: new byte[ApiRouter.MaximumRequestBodyBytes + 1])), 413, "REQUEST_TOO_LARGE");
		AssertError(router.Route(new ApiRequest("DELETE", "/api/v1/play")), 405, "METHOD_NOT_ALLOWED");
	}

	[Fact]
	public void RestartAliasValidatesHistoryIdAndQueuesRestart() {
		Guid historyId = Guid.NewGuid();
		ApiRouter router = new(this.api);

		ApiResponse response = router.Route(JsonPost($"/api/v1/history/{historyId}/play", "{}"));

		Assert.Equal(202, response.StatusCode);
		RestartRequest payload = Assert.IsType<RestartRequest>(this.api.LastPayload);
		Assert.Equal(historyId, payload.HistoryId);
		AssertError(router.Route(JsonPost("/api/v1/history/not-a-guid/play", "{}")), 400, ApiErrorCodes.InvalidRequest);
	}

	private static ApiRequest JsonPost(string path, string body)
		=> new("POST", path, ContentType: "application/json; charset=utf-8", Body: Encoding.UTF8.GetBytes(body));

	private static void AssertError(ApiResponse response, int status, string code) {
		Assert.Equal(status, response.StatusCode);
		using JsonDocument json = JsonDocument.Parse(response.Body);
		Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private sealed class FakeApi : IRemoteControlApi {
		private readonly Dictionary<Guid, RemoteCommandResult> commands = new();
		public SongQuery? LastSongQuery { get; private set; }
		public string? LastSongId { get; private set; }
		public RemoteCommandType? LastCommandType { get; private set; }
		public object? LastPayload { get; private set; }

		public HealthDto GetHealth() => new("ok", "test", true);
		public GameStateDto GetState() => new("songSelect", "song+one", ApiDifficulty.Ura, 1);

		public SongPageDto GetSongs(SongQuery query) {
			this.LastSongQuery = query;
			return new(Array.Empty<SongDto>(), query.Page, query.PageSize, 0);
		}

		public SongDto? GetSong(string songId) {
			this.LastSongId = songId;
			return songId == "song+one" ? Song(songId) : null;
		}

		public IReadOnlyList<PlayHistoryEntryDto> GetHistory(int limit) => Array.Empty<PlayHistoryEntryDto>();
		public bool TryGetCommand(Guid commandId, out RemoteCommandResult? result) => this.commands.TryGetValue(commandId, out result);

		public RemoteCommandResult Enqueue<TPayload>(RemoteCommandType type, TPayload payload) {
			this.LastCommandType = type;
			this.LastPayload = payload;
			DateTimeOffset now = DateTimeOffset.UtcNow;
			RemoteCommandResult result = new(Guid.NewGuid(), type, RemoteCommandStatus.Pending, now, now);
			this.commands[result.CommandId] = result;
			return result;
		}

		private static SongDto Song(string id) => new(
			id,
			"Song",
			new Dictionary<string, string>(),
			"",
			"Anime",
			"Maker",
			"Anime/Song",
			new[] { new SongDifficultyDto(ApiDifficulty.Ura, 10, true) },
			false);
	}
}
