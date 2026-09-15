using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class PlaylistServiceTests {
	[Fact]
	public void PlaylistPersistsOrderAndRejectsDuplicates() {
		string directory = Path.Combine(Path.GetTempPath(), $"opentaiko-playlist-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "RemotePlaylist.json");
		try {
			PlaylistService playlist = new(path);
			Assert.True(playlist.Add("song-b"));
			Assert.True(playlist.Add("song-a"));
			Assert.False(playlist.Add("song-b"));

			PlaylistService reloaded = new(path);
			Assert.Equal(new[] { "song-b", "song-a" }, reloaded.GetSongIds());
			Assert.True(reloaded.Remove("song-b"));
			Assert.Equal(new[] { "song-a" }, new PlaylistService(path).GetSongIds());
		} finally {
			Directory.Delete(directory, true);
		}
	}
}
