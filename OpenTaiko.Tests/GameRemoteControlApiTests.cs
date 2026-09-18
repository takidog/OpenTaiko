using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class GameRemoteControlApiTests {
	[Fact]
	public void BackendValidatesSongAndDifficultyBeforeQueueing() {
		using TempDirectory temp = new();
		RemoteCommandQueue queue = new();
		GameRemoteControlApi api = new("test", queue, new PlayHistoryService(temp.File("history.json")));
		api.UpdateState(new GameStateDto("SongSelect", null, null, 1));
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Oni) }));
		ApiRouter router = new(api);

		ApiResponse missing = router.Route(Post("/api/v1/play", """{"songId":"missing","difficulty":"oni"}"""));
		ApiResponse unavailable = router.Route(Post("/api/v1/play", """{"songId":"song","difficulty":"ura"}"""));
		ApiResponse valid = router.Route(Post("/api/v1/play", """{"songId":"song","difficulty":"oni"}"""));

		Assert.Equal(404, missing.StatusCode);
		Assert.Contains(ApiErrorCodes.SongNotFound, missing.BodyText);
		Assert.Equal(422, unavailable.StatusCode);
		Assert.Contains(ApiErrorCodes.InvalidDifficulty, unavailable.BodyText);
		Assert.Equal(202, valid.StatusCode);
		Assert.Single(queue.Snapshot());
	}

	[Fact]
	public void RestartResolvesExistingHistoryAndRejectsMissingHistory() {
		using TempDirectory temp = new();
		RemoteCommandQueue queue = new();
		PlayHistoryService history = new(temp.File("history.json"));
		PlayHistoryEntryDto entry = history.Start("song", ApiDifficulty.Ura, 1, 1, "left")!;
		GameRemoteControlApi api = new("test", queue, history);
		api.UpdateState(new GameStateDto("SongSelect", null, null, 1));
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Ura) }));
		ApiRouter router = new(api);

		ApiResponse valid = router.Route(Post("/api/v1/restart", $"{{\"historyId\":\"{entry.HistoryId}\"}}"));
		ApiResponse missing = router.Route(Post("/api/v1/restart", $"{{\"historyId\":\"{Guid.NewGuid()}\"}}"));

		Assert.Equal(202, valid.StatusCode);
		Assert.Equal(404, missing.StatusCode);
		Assert.Contains(ApiErrorCodes.HistoryNotFound, missing.BodyText);
	}

	[Fact]
	public void CommandsReturnConflictWhileGameIsBusy() {
		using TempDirectory temp = new();
		GameRemoteControlApi api = new("test", new RemoteCommandQueue(), new PlayHistoryService(temp.File("history.json")));
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Oni) }));
		api.UpdateState(new GameStateDto("Game", "song", ApiDifficulty.Oni, 1));

		ApiResponse response = new ApiRouter(api).Route(Post("/api/v1/play", """{"songId":"song","difficulty":"oni"}"""));

		Assert.Equal(409, response.StatusCode);
		Assert.Contains(ApiErrorCodes.GameBusy, response.BodyText);
	}

	[Fact]
	public void GameplayControlsRequireCapabilitiesPublishedByGameState() {
		using TempDirectory temp = new();
		GameRemoteControlApi api = new("test", new RemoteCommandQueue(), new PlayHistoryService(temp.File("history.json")));
		ApiRouter router = new(api);
		api.UpdateState(new GameStateDto(
			"Game", "song", ApiDifficulty.Oni, 1,
			PlaybackStatus: "playing", CanExit: true, CanRetry: true));

		ApiResponse exit = router.Route(Post("/api/v1/gameplay/exit", "{}"));
		ApiResponse retry = router.Route(Post("/api/v1/gameplay/retry", "{}"));

		Assert.Equal(202, exit.StatusCode);
		Assert.Equal(202, retry.StatusCode);
	}

	[Fact]
	public void ResultsStageAcceptsSelectionPlayPreviewAndExitCommands() {
		using TempDirectory temp = new();
		RemoteCommandQueue queue = new();
		GameRemoteControlApi api = new("test", queue, new PlayHistoryService(temp.File("history.json")));
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Oni) }));
		api.UpdateState(new GameStateDto(
			"Results", "song", ApiDifficulty.Oni, 1,
			PlaybackStatus: "results", CanSelectSong: true));
		ApiRouter router = new(api);

		ApiResponse select = router.Route(Post("/api/v1/selection", """{"songId":"song","difficulty":"oni"}"""));
		ApiResponse play = router.Route(Post("/api/v1/play", """{"songId":"song","difficulty":"oni"}"""));
		ApiResponse preview = router.Route(Post("/api/v1/preview", """{"songId":"song"}"""));
		ApiResponse exit = router.Route(Post("/api/v1/results/exit", "{}"));

		Assert.Equal(202, select.StatusCode);
		Assert.Equal(202, play.StatusCode);
		Assert.Equal(202, preview.StatusCode);
		Assert.Equal(202, exit.StatusCode);
		Assert.Equal(4, queue.Snapshot().Count);
	}

	[Theory]
	[InlineData("Game")]
	[InlineData("Results")]
	public void FavoriteCommandsAreAvailableOutsideSongSelection(string stage) {
		using TempDirectory temp = new();
		GameRemoteControlApi api = new("test", new RemoteCommandQueue(), new PlayHistoryService(temp.File("history.json")));
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Oni) }));
		api.UpdateState(new GameStateDto(stage, "song", ApiDifficulty.Oni, 1));

		ApiResponse response = new ApiRouter(api).Route(Post("/api/v1/favorite", """{"songId":"song","favorite":true}"""));

		Assert.Equal(202, response.StatusCode);
	}

	[Fact]
	public void HistoryResponseEnrichesLegacyEntriesWithCatalogMetadata() {
		using TempDirectory temp = new();
		PlayHistoryService history = new(temp.File("history.json"));
		history.Start("song", ApiDifficulty.Oni, 1, 1, "left");
		GameRemoteControlApi api = new("test", new RemoteCommandQueue(), history);
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Oni) }));

		PlayHistoryEntryDto entry = Assert.Single(api.GetHistory(100));

		Assert.Equal("Song", entry.SongTitle);
		Assert.Equal("Anime", entry.Genre);
	}

	private static ApiRequest Post(string path, string body)
		=> new("POST", path, ContentType: "application/json", Body: System.Text.Encoding.UTF8.GetBytes(body));

	private static SongDto Song(string id, ApiDifficulty difficulty) => new(
		id, "Song", new Dictionary<string, string>(), "", "Anime", "", "Anime/Song",
		new[] { new SongDifficultyDto(difficulty, 10, true) }, false);

	private sealed class TempDirectory : IDisposable {
		private readonly string path = Path.Combine(Path.GetTempPath(), $"opentaiko-api-{Guid.NewGuid():N}");
		public TempDirectory() => Directory.CreateDirectory(this.path);
		public string File(string name) => Path.Combine(this.path, name);
		public void Dispose() => Directory.Delete(this.path, true);
	}
}
