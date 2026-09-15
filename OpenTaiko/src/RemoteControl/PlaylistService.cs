using System.Text.Json;

namespace OpenTaiko.RemoteControl;

/// <summary>
/// Stores independently managed web playlists. It does not read or modify the game's
/// favorites, recent-song folders, save database, or active save slot.
/// </summary>
internal sealed class PlaylistService {
	private const int CurrentVersion = 1;
	private const int MaximumPlaylists = 100;
	private const int MaximumSongsPerPlaylist = 5000;
	private readonly object syncRoot = new();
	private readonly string? filePath;
	private readonly string? legacyFilePath;
	private readonly Func<DateTimeOffset> utcNow;
	private List<PlaylistDefinitionDto> playlists = new();

	public PlaylistService(
		string? filePath = null,
		string? legacyFilePath = null,
		Func<DateTimeOffset>? utcNow = null) {
		this.filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
		this.legacyFilePath = string.IsNullOrWhiteSpace(legacyFilePath) ? null : Path.GetFullPath(legacyFilePath);
		this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		this.Load();
	}

	public IReadOnlyList<PlaylistDefinitionDto> GetAll() {
		lock (this.syncRoot) return Array.AsReadOnly(this.playlists.Select(Clone).ToArray());
	}

	public PlaylistDefinitionDto Create(string name) {
		string normalized = NormalizeName(name);
		lock (this.syncRoot) {
			if (this.playlists.Count >= MaximumPlaylists) throw new ArgumentException($"A maximum of {MaximumPlaylists} playlists is supported.");
			DateTimeOffset now = this.utcNow();
			PlaylistDefinitionDto playlist = new(Guid.NewGuid(), normalized, now, now, Array.Empty<string>());
			this.playlists.Add(playlist);
			this.Save();
			return Clone(playlist);
		}
	}

	public PlaylistDefinitionDto Rename(Guid playlistId, string name) {
		string normalized = NormalizeName(name);
		lock (this.syncRoot) {
			int index = this.FindIndex(playlistId);
			PlaylistDefinitionDto updated = this.playlists[index] with { Name = normalized, UpdatedAtUtc = this.utcNow() };
			this.playlists[index] = updated;
			this.Save();
			return Clone(updated);
		}
	}

	public void Delete(Guid playlistId) {
		lock (this.syncRoot) {
			int index = this.FindIndex(playlistId);
			this.playlists.RemoveAt(index);
			this.Save();
		}
	}

	public PlaylistDefinitionDto AddSong(Guid playlistId, string songId) {
		ArgumentException.ThrowIfNullOrWhiteSpace(songId);
		lock (this.syncRoot) {
			int index = this.FindIndex(playlistId);
			PlaylistDefinitionDto current = this.playlists[index];
			if (current.SongIds.Contains(songId, StringComparer.Ordinal)) return Clone(current);
			if (current.SongIds.Count >= MaximumSongsPerPlaylist) throw new ArgumentException($"A playlist can contain at most {MaximumSongsPerPlaylist} songs.");
			PlaylistDefinitionDto updated = current with {
				SongIds = current.SongIds.Append(songId).ToArray(),
				UpdatedAtUtc = this.utcNow(),
			};
			this.playlists[index] = updated;
			this.Save();
			return Clone(updated);
		}
	}

	public PlaylistDefinitionDto RemoveSong(Guid playlistId, string songId) {
		ArgumentException.ThrowIfNullOrWhiteSpace(songId);
		lock (this.syncRoot) {
			int index = this.FindIndex(playlistId);
			PlaylistDefinitionDto current = this.playlists[index];
			string[] songIds = current.SongIds.Where(id => !id.Equals(songId, StringComparison.Ordinal)).ToArray();
			if (songIds.Length == current.SongIds.Count) return Clone(current);
			PlaylistDefinitionDto updated = current with { SongIds = songIds, UpdatedAtUtc = this.utcNow() };
			this.playlists[index] = updated;
			this.Save();
			return Clone(updated);
		}
	}

	public PlaylistDefinitionDto ReorderSongs(Guid playlistId, IReadOnlyList<string> songIds) {
		ArgumentNullException.ThrowIfNull(songIds);
		lock (this.syncRoot) {
			int index = this.FindIndex(playlistId);
			PlaylistDefinitionDto current = this.playlists[index];
			if (songIds.Count != songIds.Distinct(StringComparer.Ordinal).Count()
				|| !songIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
					current.SongIds.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal)) {
				throw new ArgumentException("songIds must contain every current playlist song exactly once.");
			}
			PlaylistDefinitionDto updated = current with { SongIds = songIds.ToArray(), UpdatedAtUtc = this.utcNow() };
			this.playlists[index] = updated;
			this.Save();
			return Clone(updated);
		}
	}

	public PlaylistExportDto Export() {
		lock (this.syncRoot) return new(CurrentVersion, Array.AsReadOnly(this.playlists.Select(Clone).ToArray()));
	}

	public void Import(PlaylistExportDto document) {
		ArgumentNullException.ThrowIfNull(document);
		if (document.Playlists is null) throw new ArgumentException("playlists is required.");
		if (document.Version != CurrentVersion) throw new ArgumentException($"Unsupported playlist export version {document.Version}.");
		if (document.Playlists.Count > MaximumPlaylists) throw new ArgumentException($"A maximum of {MaximumPlaylists} playlists is supported.");

		HashSet<Guid> ids = new();
		List<PlaylistDefinitionDto> imported = new();
		foreach (PlaylistDefinitionDto playlist in document.Playlists) {
			if (playlist.Id == Guid.Empty || !ids.Add(playlist.Id)) throw new ArgumentException("Playlist IDs must be non-empty and unique.");
			string name = NormalizeName(playlist.Name);
			string[] songIds = playlist.SongIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
			if (songIds.Length > MaximumSongsPerPlaylist) throw new ArgumentException($"A playlist can contain at most {MaximumSongsPerPlaylist} songs.");
			imported.Add(new PlaylistDefinitionDto(playlist.Id, name, playlist.CreatedAtUtc, this.utcNow(), songIds));
		}
		lock (this.syncRoot) {
			this.playlists = imported;
			this.Save();
		}
	}

	private int FindIndex(Guid playlistId) {
		int index = this.playlists.FindIndex(playlist => playlist.Id == playlistId);
		return index >= 0 ? index : throw new ApiRouteException(404, "PLAYLIST_NOT_FOUND", "The requested playlist was not found.");
	}

	private void Load() {
		if (this.filePath is null) return;
		if (!File.Exists(this.filePath)) {
			if (this.TryMigrateLegacy()) return;
			this.Create("我的清單");
			return;
		}
		try {
			PlaylistExportDto? document = JsonSerializer.Deserialize<PlaylistExportDto>(File.ReadAllBytes(this.filePath), RemoteControlJson.Options);
			if (document is not null) this.Import(document);
		} catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException) {
			string corruptPath = this.filePath + $".corrupt-{this.utcNow():yyyyMMddHHmmssfff}";
			File.Move(this.filePath, corruptPath, true);
			this.playlists = new();
			this.Create("我的清單");
		}
	}

	private bool TryMigrateLegacy() {
		if (this.legacyFilePath is null || !File.Exists(this.legacyFilePath)) return false;
		try {
			string[] songIds = (JsonSerializer.Deserialize<List<string>>(
				File.ReadAllBytes(this.legacyFilePath), RemoteControlJson.Options) ?? new())
				.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
			DateTimeOffset now = this.utcNow();
			this.playlists.Add(new PlaylistDefinitionDto(Guid.NewGuid(), "我的清單", now, now, songIds));
			this.Save();
			return true;
		} catch (JsonException) {
			return false;
		}
	}

	private void Save() {
		if (this.filePath is null) return;
		string? directory = Path.GetDirectoryName(this.filePath);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		string tempPath = this.filePath + $".tmp-{Guid.NewGuid():N}";
		try {
			File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(
				new PlaylistExportDto(CurrentVersion, this.playlists), RemoteControlJson.Options));
			File.Move(tempPath, this.filePath, true);
		} finally {
			if (File.Exists(tempPath)) File.Delete(tempPath);
		}
	}

	private static string NormalizeName(string name) {
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		string normalized = name.Trim();
		if (normalized.Length > 80) throw new ArgumentException("Playlist names cannot exceed 80 characters.");
		return normalized;
	}

	private static PlaylistDefinitionDto Clone(PlaylistDefinitionDto playlist)
		=> playlist with { SongIds = Array.AsReadOnly(playlist.SongIds.ToArray()) };
}
