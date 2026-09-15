using System.Text.Json;

namespace OpenTaiko.RemoteControl;

/// <summary>
/// Stores the web remote's ordered, user-selected song list independently from play history.
/// </summary>
internal sealed class PlaylistService {
	private readonly object syncRoot = new();
	private readonly string? filePath;
	private List<string> songIds = new();

	public PlaylistService(string? filePath = null) {
		this.filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
		this.Load();
	}

	public IReadOnlyList<string> GetSongIds() {
		lock (this.syncRoot) return Array.AsReadOnly(this.songIds.ToArray());
	}

	public bool Add(string songId) {
		ArgumentException.ThrowIfNullOrWhiteSpace(songId);
		lock (this.syncRoot) {
			if (this.songIds.Contains(songId, StringComparer.Ordinal)) return false;
			this.songIds.Add(songId);
			this.Save();
			return true;
		}
	}

	public bool Remove(string songId) {
		ArgumentException.ThrowIfNullOrWhiteSpace(songId);
		lock (this.syncRoot) {
			bool removed = this.songIds.Remove(songId);
			if (removed) this.Save();
			return removed;
		}
	}

	private void Load() {
		if (this.filePath is null || !File.Exists(this.filePath)) return;
		try {
			this.songIds = JsonSerializer.Deserialize<List<string>>(
				File.ReadAllBytes(this.filePath), RemoteControlJson.Options) ?? new();
			this.songIds = this.songIds
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.Distinct(StringComparer.Ordinal)
				.ToList();
		} catch (JsonException) {
			string corruptPath = this.filePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
			File.Move(this.filePath, corruptPath, true);
			this.songIds = new();
			this.Save();
		}
	}

	private void Save() {
		if (this.filePath is null) return;
		string? directory = Path.GetDirectoryName(this.filePath);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		string tempPath = this.filePath + $".tmp-{Guid.NewGuid():N}";
		try {
			File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(this.songIds, RemoteControlJson.Options));
			File.Move(tempPath, this.filePath, true);
		} finally {
			if (File.Exists(tempPath)) File.Delete(tempPath);
		}
	}
}
