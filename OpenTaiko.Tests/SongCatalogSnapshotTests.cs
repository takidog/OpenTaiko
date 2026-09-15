using System.Text.Json;
using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class SongCatalogSnapshotTests {
	[Fact]
	public void SnapshotMapsUnicodeTitlesAndUraWithoutPaths() {
		string uniqueIdPath = Path.Combine(Path.GetTempPath(), $"song-id-{Guid.NewGuid():N}.json");
		try {
			CSongListNode node = SongNode(uniqueIdPath, "song-one", "太鼓の曲", "Anime", 10, Difficulty.Edit);
			node.ldTitle.SetString("ja", "太鼓の曲");
			node.ldTitle.SetString("zh-tw", "太鼓之歌");
			node.score[(int)Difficulty.Edit].ファイル情報.ファイルの絶対パス = @"C:\secret\chart.tja";

			SongCatalogSnapshot snapshot = SongCatalogSnapshot.FromSongNodes(new[] { node }, _ => true);
			SongDto song = Assert.IsType<SongDto>(snapshot.GetSong("song-one"));

			Assert.Equal("太鼓之歌", song.Titles["zh-tw"]);
			Assert.Contains(song.Difficulties, difficulty => difficulty.Id == ApiDifficulty.Ura && difficulty.Level == 10);
			Assert.True(song.Favorite);
			Assert.DoesNotContain("secret", JsonSerializer.Serialize(song, RemoteControlJson.Options), StringComparison.OrdinalIgnoreCase);
		} finally {
			File.Delete(uniqueIdPath);
		}
	}

	[Fact]
	public void SearchFiltersAcrossMetadataDifficultyAndLevel() {
		string firstPath = Path.Combine(Path.GetTempPath(), $"song-id-{Guid.NewGuid():N}.json");
		string secondPath = Path.Combine(Path.GetTempPath(), $"song-id-{Guid.NewGuid():N}.json");
		try {
			CSongListNode first = SongNode(firstPath, "one", "Alpha", "Anime", 9, Difficulty.Oni);
			first.strMaker = "製作者";
			CSongListNode second = SongNode(secondPath, "two", "Beta", "Game Music", 6, Difficulty.Hard);
			SongCatalogSnapshot snapshot = SongCatalogSnapshot.FromSongNodes(new[] { second, first });

			SongPageDto text = snapshot.Search(new SongQuery("製作者", null, null, null, null, 1, 100));
			SongPageDto chart = snapshot.Search(new SongQuery(null, "anime", ApiDifficulty.Oni, 8, 10, 1, 100));
			SongPageDto noMatch = snapshot.Search(new SongQuery(null, null, ApiDifficulty.Ura, null, null, 1, 100));

			Assert.Equal("one", Assert.Single(text.Items).Id);
			Assert.Equal("one", Assert.Single(chart.Items).Id);
			Assert.Empty(noMatch.Items);
		} finally {
			File.Delete(firstPath);
			File.Delete(secondPath);
		}
	}

	[Fact]
	public void SearchPaginatesInStableTitleOrder() {
		List<string> paths = new();
		try {
			CSongListNode[] nodes = new[] { "Charlie", "Alpha", "Bravo" }
				.Select((title, index) => {
					string path = Path.Combine(Path.GetTempPath(), $"song-id-{Guid.NewGuid():N}.json");
					paths.Add(path);
					return SongNode(path, $"song-{index}", title, "Anime", 5, Difficulty.Normal);
				})
				.ToArray();
			SongCatalogSnapshot snapshot = SongCatalogSnapshot.FromSongNodes(nodes);

			SongPageDto page = snapshot.Search(new SongQuery(null, null, null, null, null, 2, 1));

			Assert.Equal(3, page.Total);
			Assert.Equal("Bravo", Assert.Single(page.Items).Title);
		} finally {
			foreach (string path in paths) File.Delete(path);
		}
	}

	private static CSongListNode SongNode(
		string uniqueIdPath,
		string id,
		string title,
		string genre,
		int level,
		Difficulty difficulty) {
		CSongUniqueID uniqueId = new(uniqueIdPath);
		uniqueId.data.id = id;
		CSongListNode node = new() {
			nodeType = CSongListNode.ENodeType.SCORE,
			uniqueId = uniqueId,
			songGenre = genre,
			strBreadcrumbs = $"{genre}/{title}",
		};
		node.ldTitle.SetString("default", title);
		node.score[(int)difficulty] = new CScore();
		node.nLevel[(int)difficulty] = level;
		return node;
	}
}
