using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class GameRemoteControlApiTests {
	[Fact]
	public void BackendValidatesSongAndDifficultyBeforeQueueing() {
		using TempDirectory temp = new();
		RemoteCommandQueue queue = new();
		GameRemoteControlApi api = new("test", queue, new PlayHistoryService(temp.File("history.json")));
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
		api.ReplaceCatalog(new SongCatalogSnapshot(new[] { Song("song", ApiDifficulty.Ura) }));
		ApiRouter router = new(api);

		ApiResponse valid = router.Route(Post("/api/v1/restart", $"{{\"historyId\":\"{entry.HistoryId}\"}}"));
		ApiResponse missing = router.Route(Post("/api/v1/restart", $"{{\"historyId\":\"{Guid.NewGuid()}\"}}"));

		Assert.Equal(202, valid.StatusCode);
		Assert.Equal(404, missing.StatusCode);
		Assert.Contains(ApiErrorCodes.HistoryNotFound, missing.BodyText);
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
