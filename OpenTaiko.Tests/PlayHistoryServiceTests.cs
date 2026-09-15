using System.Text;
using System.Text.Json;
using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class PlayHistoryServiceTests {
	[Fact]
	public void RepeatedSongAndDifficultyCreatesDistinctEvents() {
		using TempHistory temp = new();
		Queue<Guid> ids = new(new[] { Guid.NewGuid(), Guid.NewGuid() });
		PlayHistoryService history = new(temp.Path, newId: () => ids.Dequeue());

		PlayHistoryEntryDto first = history.Start("song", ApiDifficulty.Ura, 1, 1, "right")!;
		PlayHistoryEntryDto second = history.Start("song", ApiDifficulty.Ura, 1, 1, "right")!;

		Assert.NotEqual(first.HistoryId, second.HistoryId);
		Assert.Equal(new[] { second.HistoryId, first.HistoryId }, history.GetRecent().Select(entry => entry.HistoryId));
		Assert.All(history.GetRecent(), entry => Assert.Equal(ApiDifficulty.Ura, entry.Difficulty));
	}

	[Fact]
	public void KeepsOnlyNewestConfiguredEntriesAcrossReload() {
		using TempHistory temp = new();
		DateTimeOffset now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
		PlayHistoryService history = new(temp.Path, maximumEntries: 3, utcNow: () => now = now.AddMinutes(1));
		for (int index = 0; index < 5; index++) history.Start($"song-{index}", ApiDifficulty.Oni, 1, 1, "left");

		PlayHistoryService loaded = new(temp.Path, maximumEntries: 3);

		Assert.Equal(new[] { "song-4", "song-3", "song-2" }, loaded.GetRecent().Select(entry => entry.SongId));
	}

	[Fact]
	public void CompleteUpdatesSameEntryAndPersistsResult() {
		using TempHistory temp = new();
		DateTimeOffset now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
		PlayHistoryService history = new(temp.Path, utcNow: () => now = now.AddMinutes(1));
		PlayHistoryEntryDto started = history.Start("song", ApiDifficulty.Hard, 2, 2, "left")!;
		Dictionary<string, JsonElement> result = new() {
			["score"] = JsonSerializer.SerializeToElement(123456),
			["clear"] = JsonSerializer.SerializeToElement(true),
		};

		Assert.True(history.Complete(started.HistoryId, "cleared", result));
		PlayHistoryEntryDto completed = new PlayHistoryService(temp.Path).Get(started.HistoryId)!;

		Assert.Equal("cleared", completed.Status);
		Assert.NotNull(completed.CompletedAtUtc);
		Assert.Equal(123456, completed.Modifiers["score"].GetInt32());
	}

	[Fact]
	public void CorruptJsonIsBackedUpAndReplacedWithEmptyHistory() {
		using TempHistory temp = new();
		File.WriteAllText(temp.Path, "{not-json", Encoding.UTF8);

		PlayHistoryService history = new(temp.Path, utcNow: () => new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

		Assert.Empty(history.GetRecent());
		Assert.Empty(JsonSerializer.Deserialize<List<PlayHistoryEntryDto>>(File.ReadAllBytes(temp.Path), RemoteControlJson.Options)!);
		Assert.Single(Directory.GetFiles(temp.Directory, "PlayHistory.json.corrupt-20260915120000000"));
	}

	[Fact]
	public void DisabledHistoryDoesNotCreateAFile() {
		using TempHistory temp = new();
		PlayHistoryService history = new(temp.Path, enabled: false);

		Assert.Null(history.Start("song", ApiDifficulty.Oni, 1, 1, "left"));
		Assert.False(File.Exists(temp.Path));
	}

	private sealed class TempHistory : IDisposable {
		public TempHistory() {
			this.Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"opentaiko-history-{Guid.NewGuid():N}");
			System.IO.Directory.CreateDirectory(this.Directory);
			this.Path = System.IO.Path.Combine(this.Directory, "PlayHistory.json");
		}

		public string Directory { get; }
		public string Path { get; }

		public void Dispose() => System.IO.Directory.Delete(this.Directory, recursive: true);
	}
}
