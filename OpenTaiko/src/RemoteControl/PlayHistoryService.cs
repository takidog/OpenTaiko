using System.Collections.ObjectModel;
using System.Text.Json;

namespace OpenTaiko.RemoteControl;

internal sealed class PlayHistoryService {
	private readonly object syncRoot = new();
	private readonly string filePath;
	private readonly bool enabled;
	private readonly int maximumEntries;
	private readonly Func<DateTimeOffset> utcNow;
	private readonly Func<Guid> newId;
	private List<PlayHistoryEntryDto> entries = new();
	public event Action<PlayHistoryEntryDto>? HistoryChanged;

	public PlayHistoryService(
		string filePath,
		bool enabled = true,
		int maximumEntries = 100,
		Func<DateTimeOffset>? utcNow = null,
		Func<Guid>? newId = null) {
		this.filePath = Path.GetFullPath(filePath);
		this.enabled = enabled;
		this.maximumEntries = Math.Clamp(maximumEntries, 1, 100);
		this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		this.newId = newId ?? Guid.NewGuid;
		this.Load();
	}

	public PlayHistoryEntryDto? Start(
		string songId,
		ApiDifficulty difficulty,
		int saveSlot,
		int playerCount,
		string playerSide,
		IReadOnlyDictionary<string, JsonElement>? modifiers = null) {
		if (!this.enabled) return null;
		ArgumentException.ThrowIfNullOrWhiteSpace(songId);
		if (saveSlot is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(saveSlot));
		if (playerCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(playerCount));
		if (!playerSide.Equals("left", StringComparison.OrdinalIgnoreCase)
			&& !playerSide.Equals("right", StringComparison.OrdinalIgnoreCase)) {
			throw new ArgumentException("Player side must be left or right.", nameof(playerSide));
		}

		PlayHistoryEntryDto entry = new(
			this.newId(),
			songId,
			difficulty,
			saveSlot,
			playerCount,
			playerSide.ToLowerInvariant(),
			this.utcNow(),
			null,
			"started",
			FreezeModifiers(modifiers));
		lock (this.syncRoot) {
			this.entries.Insert(0, entry);
			this.Trim();
			this.Save();
		}
		this.HistoryChanged?.Invoke(entry);
		return entry;
	}

	public bool Complete(Guid historyId, string status, IReadOnlyDictionary<string, JsonElement>? result = null) {
		if (!this.enabled) return false;
		if (status is not ("completed" or "cleared" or "failed" or "aborted")) {
			throw new ArgumentException("Unsupported completion status.", nameof(status));
		}

		PlayHistoryEntryDto completed;
		lock (this.syncRoot) {
			int index = this.entries.FindIndex(entry => entry.HistoryId == historyId);
			if (index < 0) return false;
			PlayHistoryEntryDto current = this.entries[index];
			Dictionary<string, JsonElement> merged = new(current.Modifiers, StringComparer.Ordinal);
			if (result is not null) {
				foreach ((string key, JsonElement value) in result) merged[key] = value.Clone();
			}
			completed = current with {
				CompletedAtUtc = this.utcNow(),
				Status = status,
				Modifiers = new ReadOnlyDictionary<string, JsonElement>(merged),
			};
			this.entries[index] = completed;
			this.Save();
		}
		this.HistoryChanged?.Invoke(completed);
		return true;
	}

	public PlayHistoryEntryDto? Get(Guid historyId) {
		lock (this.syncRoot) return this.entries.FirstOrDefault(entry => entry.HistoryId == historyId);
	}

	public PlayHistoryEntryDto? GetLatest() {
		lock (this.syncRoot) return this.entries.FirstOrDefault();
	}

	public IReadOnlyList<PlayHistoryEntryDto> GetRecent(int limit = 100) {
		int safeLimit = Math.Clamp(limit, 1, 100);
		lock (this.syncRoot) return Array.AsReadOnly(this.entries.Take(safeLimit).ToArray());
	}

	private void Load() {
		if (!this.enabled || !File.Exists(this.filePath)) return;
		try {
			byte[] json = File.ReadAllBytes(this.filePath);
			this.entries = JsonSerializer.Deserialize<List<PlayHistoryEntryDto>>(json, RemoteControlJson.Options) ?? new();
			this.entries = this.entries.OrderByDescending(entry => entry.StartedAtUtc).ToList();
			this.Trim();
		} catch (Exception exception) when (exception is JsonException or NotSupportedException) {
			string corruptPath = this.filePath + $".corrupt-{this.utcNow():yyyyMMddHHmmssfff}";
			File.Move(this.filePath, corruptPath, true);
			this.entries = new();
			this.Save();
		}
	}

	private void Trim() {
		if (this.entries.Count > this.maximumEntries) {
			this.entries.RemoveRange(this.maximumEntries, this.entries.Count - this.maximumEntries);
		}
	}

	private void Save() {
		string? directory = Path.GetDirectoryName(this.filePath);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		string tempPath = this.filePath + $".tmp-{Guid.NewGuid():N}";
		try {
			using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
				JsonSerializer.Serialize(stream, this.entries, RemoteControlJson.Options);
				stream.Flush(flushToDisk: true);
			}
			if (File.Exists(this.filePath)) {
				try {
					File.Replace(tempPath, this.filePath, null);
				} catch (PlatformNotSupportedException) {
					File.Move(tempPath, this.filePath, true);
				}
			} else {
				File.Move(tempPath, this.filePath);
			}
		} finally {
			if (File.Exists(tempPath)) File.Delete(tempPath);
		}
	}

	private static IReadOnlyDictionary<string, JsonElement> FreezeModifiers(IReadOnlyDictionary<string, JsonElement>? modifiers) {
		Dictionary<string, JsonElement> copy = modifiers?.ToDictionary(
			pair => pair.Key,
			pair => pair.Value.Clone(),
			StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
		return new ReadOnlyDictionary<string, JsonElement>(copy);
	}
}
