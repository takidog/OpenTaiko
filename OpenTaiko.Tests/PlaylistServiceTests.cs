using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class PlaylistServiceTests {
	[Fact]
	public void SupportsMultiplePlaylistsCrudOrderingAndPersistence() {
		using TempDirectory temp = new();
		DateTimeOffset now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
		PlaylistService service = new(temp.File("RemotePlaylists.json"), utcNow: () => now = now.AddMinutes(1));
		PlaylistDefinitionDto initial = Assert.Single(service.GetAll());
		PlaylistDefinitionDto roadTrip = service.Create("Road Trip");
		service.AddSong(roadTrip.Id, "song-a");
		service.AddSong(roadTrip.Id, "song-b");
		service.AddSong(roadTrip.Id, "song-a");
		service.ReorderSongs(roadTrip.Id, new[] { "song-b", "song-a" });
		service.Rename(roadTrip.Id, "Favorites");
		service.Delete(initial.Id);

		PlaylistDefinitionDto loaded = Assert.Single(new PlaylistService(temp.File("RemotePlaylists.json")).GetAll());
		Assert.Equal("Favorites", loaded.Name);
		Assert.Equal(new[] { "song-b", "song-a" }, loaded.SongIds);
	}

	[Fact]
	public void AllowsDeletingLastPlaylistAndRoundTripsExportImport() {
		using TempDirectory first = new();
		using TempDirectory second = new();
		PlaylistService source = new(first.File("RemotePlaylists.json"));
		PlaylistDefinitionDto playlist = Assert.Single(source.GetAll());
		source.AddSong(playlist.Id, "song-a");
		PlaylistExportDto export = source.Export();

		PlaylistService target = new(second.File("RemotePlaylists.json"));
		target.Import(export);
		Assert.Equal("song-a", Assert.Single(Assert.Single(target.GetAll()).SongIds));

		target.Delete(playlist.Id);
		Assert.Empty(target.GetAll());
		Assert.Empty(new PlaylistService(second.File("RemotePlaylists.json")).GetAll());
	}

	[Fact]
	public void MigratesLegacySinglePlaylistFile() {
		using TempDirectory temp = new();
		File.WriteAllText(temp.File("RemotePlaylist.json"), "[\"song-a\",\"song-b\",\"song-a\"]");

		PlaylistService service = new(temp.File("RemotePlaylists.json"), temp.File("RemotePlaylist.json"));

		PlaylistDefinitionDto migrated = Assert.Single(service.GetAll());
		Assert.Equal("我的清單", migrated.Name);
		Assert.Equal(new[] { "song-a", "song-b" }, migrated.SongIds);
		Assert.True(File.Exists(temp.File("RemotePlaylists.json")));
	}

	private sealed class TempDirectory : IDisposable {
		private readonly string path = Path.Combine(Path.GetTempPath(), $"opentaiko-playlists-{Guid.NewGuid():N}");
		public TempDirectory() => Directory.CreateDirectory(this.path);
		public string File(string name) => Path.Combine(this.path, name);
		public void Dispose() => Directory.Delete(this.path, true);
	}
}
