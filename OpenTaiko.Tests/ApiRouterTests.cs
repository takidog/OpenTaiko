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
		Assert.Contains("\"playbackStatus\":\"playing\"", state.BodyText);
		Assert.Contains("\"progress\":0.5", state.BodyText);
		Assert.Contains("\"canRetry\":true", state.BodyText);
	}

	[Fact]
	public void SongsParsesFiltersPaginationAndEncodedIds() {
		ApiRouter router = new(this.api);

		ApiResponse list = router.Route(new ApiRequest(
			"GET",
			"/api/v1/songs",
			"?query=%E5%A4%AA%E9%BC%93&genre=Anime&difficulty=ura&minLevel=8&maxLevel=10&favorite=true&page=2&pageSize=25"));
		ApiResponse item = router.Route(new ApiRequest("GET", "/api/v1/songs/song%2Bone"));

		Assert.Equal(200, list.StatusCode);
		Assert.Equal("太鼓", this.api.LastSongQuery!.Query);
		Assert.Equal(ApiDifficulty.Ura, this.api.LastSongQuery.Difficulty);
		Assert.Equal(2, this.api.LastSongQuery.Page);
		Assert.Equal(25, this.api.LastSongQuery.PageSize);
		Assert.True(this.api.LastSongQuery.Favorite);
		Assert.Equal(200, item.StatusCode);
		Assert.Equal("song+one", this.api.LastSongId);
	}

	[Theory]
	[InlineData("?difficulty=impossible", 422, "INVALID_DIFFICULTY")]
	[InlineData("?pageSize=501", 400, "INVALID_REQUEST")]
	[InlineData("?minLevel=10&maxLevel=2", 400, "INVALID_REQUEST")]
	[InlineData("?favorite=yes", 400, "INVALID_REQUEST")]
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

	[Fact]
	public void BodylessCommandsDoNotRequireContentType() {
		ApiRouter router = new(this.api);

		ApiResponse stop = router.Route(new ApiRequest("POST", "/api/v1/preview/stop"));
		ApiResponse restart = router.Route(new ApiRequest("POST", "/api/v1/restart"));
		ApiResponse exit = router.Route(new ApiRequest("POST", "/api/v1/gameplay/exit"));
		Assert.Equal(RemoteCommandType.ExitGameplay, this.api.LastCommandType);
		ApiResponse retry = router.Route(new ApiRequest("POST", "/api/v1/gameplay/retry"));

		Assert.Equal(202, stop.StatusCode);
		Assert.Equal(202, restart.StatusCode);
		Assert.Equal(202, exit.StatusCode);
		Assert.Equal(202, retry.StatusCode);
		Assert.Equal(RemoteCommandType.RetryGameplay, this.api.LastCommandType);
	}

	[Fact]
	public void GenresAndBrowserAudioAreExposedWithoutLeakingPaths() {
		ApiRouter router = new(this.api);

		ApiResponse genres = router.Route(new ApiRequest("GET", "/api/v1/genres"));
		ApiResponse audio = router.Route(new ApiRequest("GET", "/api/v1/songs/song%2Bone/audio"));

		Assert.Equal(200, genres.StatusCode);
		Assert.Contains("Anime", genres.BodyText);
		Assert.Equal(200, audio.StatusCode);
		Assert.Equal("audio/ogg", audio.ContentType);
		Assert.Equal(new byte[] { 1, 2, 3 }, audio.Body);
	}

	[Fact]
	public void FavoriteAndPlaylistCrudUseStableEndpoints() {
		ApiRouter router = new(this.api);

		ApiResponse favorite = router.Route(JsonPost("/api/v1/favorite", """{"songId":"song+one","favorite":true}"""));
		ApiResponse created = router.Route(JsonPost("/api/v1/playlists", """{"name":"Road Trip"}"""));
		Guid playlistId = JsonDocument.Parse(created.Body).RootElement.GetProperty("id").GetGuid();
		ApiResponse added = router.Route(JsonPost($"/api/v1/playlists/{playlistId}/songs", """{"songId":"song+one"}"""));
		ApiResponse renamed = router.Route(JsonRequest("PATCH", $"/api/v1/playlists/{playlistId}", """{"name":"Favorites"}"""));
		ApiResponse reordered = router.Route(JsonRequest("PUT", $"/api/v1/playlists/{playlistId}/songs", """{"songIds":["song+one"]}"""));
		ApiResponse exported = router.Route(new ApiRequest("GET", "/api/v1/playlists/export"));
		ApiResponse imported = router.Route(JsonRequest("PUT", "/api/v1/playlists/import", exported.BodyText));
		ApiResponse removed = router.Route(new ApiRequest("DELETE", $"/api/v1/playlists/{playlistId}/songs/song%2Bone"));
		ApiResponse deleted = router.Route(new ApiRequest("DELETE", $"/api/v1/playlists/{playlistId}"));

		Assert.Equal(202, favorite.StatusCode);
		Assert.Equal(RemoteCommandType.SetFavorite, this.api.LastCommandType);
		Assert.True(Assert.IsType<FavoriteRequest>(this.api.LastPayload).Favorite);
		Assert.Equal(201, created.StatusCode);
		Assert.Equal(200, added.StatusCode);
		using (JsonDocument json = JsonDocument.Parse(added.Body)) {
			Assert.Equal("song+one", json.RootElement.GetProperty("songs")[0].GetProperty("id").GetString());
		}
		Assert.Contains("Favorites", renamed.BodyText);
		Assert.Equal(200, reordered.StatusCode);
		Assert.Contains("\"version\":1", exported.BodyText);
		Assert.Equal(200, imported.StatusCode);
		Assert.Equal(200, removed.StatusCode);
		Assert.Empty(JsonDocument.Parse(removed.Body).RootElement.GetProperty("songs").EnumerateArray());
		Assert.Empty(JsonDocument.Parse(deleted.Body).RootElement.EnumerateArray());
	}

	private static ApiRequest JsonPost(string path, string body)
		=> new("POST", path, ContentType: "application/json; charset=utf-8", Body: Encoding.UTF8.GetBytes(body));
	private static ApiRequest JsonRequest(string method, string path, string body)
		=> new(method, path, ContentType: "application/json; charset=utf-8", Body: Encoding.UTF8.GetBytes(body));

	private static void AssertError(ApiResponse response, int status, string code) {
		Assert.Equal(status, response.StatusCode);
		using JsonDocument json = JsonDocument.Parse(response.Body);
		Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private sealed class FakeApi : IRemoteControlApi {
		private readonly Dictionary<Guid, RemoteCommandResult> commands = new();
		private readonly List<PlaylistDto> playlists = new();
		public SongQuery? LastSongQuery { get; private set; }
		public string? LastSongId { get; private set; }
		public RemoteCommandType? LastCommandType { get; private set; }
		public object? LastPayload { get; private set; }

		public HealthDto GetHealth() => new("ok", "test", true);
		public GameStateDto GetState() => new(
			"Game", "song+one", ApiDifficulty.Ura, 1,
			"playing", "Song", 30_000, 60_000, 0.5, true, true, false);

		public SongPageDto GetSongs(SongQuery query) {
			this.LastSongQuery = query;
			return new(Array.Empty<SongDto>(), query.Page, query.PageSize, 0);
		}

		public SongDto? GetSong(string songId) {
			this.LastSongId = songId;
			return songId == "song+one" ? Song(songId) : null;
		}
		public SongAudioDto? GetSongAudio(string songId)
			=> songId == "song+one" ? new SongAudioDto("audio/ogg", new byte[] { 1, 2, 3 }) : null;
		public IReadOnlyList<string> GetGenres() => new[] { "Anime", "Game Music" };
		public IReadOnlyList<PlaylistDto> GetPlaylists() => this.playlists;
		public PlaylistExportDto ExportPlaylists() => new(1, this.playlists.Select(item =>
			new PlaylistDefinitionDto(item.Id, item.Name, item.CreatedAtUtc, item.UpdatedAtUtc, item.Songs.Select(song => song.Id).ToArray())).ToArray());
		public PlaylistDto CreatePlaylist(string name) {
			PlaylistDto created = new(Guid.NewGuid(), name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Array.Empty<SongDto>());
			this.playlists.Add(created);
			return created;
		}
		public PlaylistDto RenamePlaylist(Guid playlistId, string name) => this.Update(playlistId, item => item with { Name = name });
		public void DeletePlaylist(Guid playlistId) => this.playlists.RemoveAll(item => item.Id == playlistId);
		public PlaylistDto AddSongToPlaylist(Guid playlistId, string songId)
			=> this.Update(playlistId, item => item with { Songs = item.Songs.Append(Song(songId)).ToArray() });
		public PlaylistDto RemoveSongFromPlaylist(Guid playlistId, string songId)
			=> this.Update(playlistId, item => item with { Songs = item.Songs.Where(song => song.Id != songId).ToArray() });
		public PlaylistDto ReorderPlaylist(Guid playlistId, IReadOnlyList<string> songIds)
			=> this.Update(playlistId, item => item with { Songs = songIds.Select(Song).ToArray() });
		public void ImportPlaylists(PlaylistExportDto document) { }

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

		private PlaylistDto Update(Guid id, Func<PlaylistDto, PlaylistDto> update) {
			int index = this.playlists.FindIndex(item => item.Id == id);
			this.playlists[index] = update(this.playlists[index]);
			return this.playlists[index];
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
