using System.Collections.ObjectModel;

namespace OpenTaiko.RemoteControl;

/// <summary>
/// An immutable, path-free copy of the song catalog for use by HTTP threads.
/// Local preview paths are retained separately and are never serialized.
/// </summary>
internal sealed class SongCatalogSnapshot {
	private readonly SongDto[] songs;
	private readonly IReadOnlyDictionary<string, SongDto> songsById;
	private readonly IReadOnlyDictionary<string, string> previewPaths;

	public static readonly SongCatalogSnapshot Empty = new(Array.Empty<SongDto>());

	public SongCatalogSnapshot(IEnumerable<SongDto> songs, IReadOnlyDictionary<string, string>? previewPaths = null) {
		this.songs = songs.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase).ToArray();
		this.songsById = new ReadOnlyDictionary<string, SongDto>(
			this.songs.ToDictionary(song => song.Id, StringComparer.Ordinal));
		this.previewPaths = new ReadOnlyDictionary<string, string>(
			new Dictionary<string, string>(previewPaths ?? new Dictionary<string, string>(), StringComparer.Ordinal));
	}

	public int Count => this.songs.Length;

	public static SongCatalogSnapshot FromSongNodes(
		IEnumerable<CSongListNode> nodes,
		Func<string, bool>? isFavorite = null) {
		isFavorite ??= _ => false;
		List<SongDto> dtos = new();
		Dictionary<string, string> previewPaths = new(StringComparer.Ordinal);
		foreach (CSongListNode node in nodes.Where(node =>
			node.nodeType == CSongListNode.ENodeType.SCORE && !string.IsNullOrWhiteSpace(node.tGetUniqueId()))) {
			string id = node.tGetUniqueId();
			string? previewPath = FindPreviewPath(node);
			dtos.Add(FromSongNode(node, isFavorite(id), previewPath is not null));
			if (previewPath is not null) previewPaths[id] = previewPath;
		}
		return new SongCatalogSnapshot(dtos, previewPaths);
	}

	public SongDto? GetSong(string songId)
		=> this.songsById.TryGetValue(songId, out SongDto? song) ? song : null;

	public IReadOnlyList<string> GetGenres()
		=> Array.AsReadOnly(this.songs.Select(song => song.Genre)
			.Where(genre => !string.IsNullOrWhiteSpace(genre))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
			.ToArray());

	public IReadOnlyList<SongDto> GetSongs(IEnumerable<string> songIds)
		=> Array.AsReadOnly(songIds
			.Select(this.GetSong)
			.Where(song => song is not null)
			.Cast<SongDto>()
			.ToArray());

	public SongCatalogSnapshot WithFavorite(string songId, bool favorite) {
		if (!this.songsById.TryGetValue(songId, out SongDto? current) || current.Favorite == favorite) return this;
		return new SongCatalogSnapshot(
			this.songs.Select(song => song.Id == songId ? song with { Favorite = favorite } : song),
			this.previewPaths);
	}

	public SongAudioDto? GetPreviewAudio(string songId) {
		if (!this.previewPaths.TryGetValue(songId, out string? path) || !File.Exists(path)) return null;
		return new SongAudioDto(AudioContentType(path), File.ReadAllBytes(path));
	}

	public SongPageDto Search(SongQuery query) {
		IEnumerable<SongDto> filtered = this.songs;
		if (!string.IsNullOrWhiteSpace(query.Query)) {
			string text = query.Query.Trim();
			filtered = filtered.Where(song => SearchableText(song).Contains(text, StringComparison.OrdinalIgnoreCase));
		}
		if (!string.IsNullOrWhiteSpace(query.Genre)) {
			filtered = filtered.Where(song => song.Genre.Equals(query.Genre, StringComparison.OrdinalIgnoreCase));
		}
		if (query.Favorite is bool favorite) {
			filtered = filtered.Where(song => song.Favorite == favorite);
		}
		if (query.Difficulty is ApiDifficulty difficulty) {
			filtered = filtered.Where(song => song.Difficulties.Any(chart => chart.Id == difficulty && chart.Available));
		}
		if (query.MinimumLevel is int minimum) {
			filtered = filtered.Where(song => MatchingCharts(song, query.Difficulty).Any(chart => chart.Available && chart.Level >= minimum));
		}
		if (query.MaximumLevel is int maximum) {
			filtered = filtered.Where(song => MatchingCharts(song, query.Difficulty).Any(chart => chart.Available && chart.Level <= maximum));
		}

		SongDto[] matches = filtered.ToArray();
		SongDto[] page = matches
			.Skip((query.Page - 1) * query.PageSize)
			.Take(query.PageSize)
			.ToArray();
		return new SongPageDto(Array.AsReadOnly(page), query.Page, query.PageSize, matches.Length);
	}

	private static SongDto FromSongNode(CSongListNode node, bool favorite, bool webPreviewAvailable) {
		List<SongDifficultyDto> difficulties = new();
		for (int index = 0; index < (int)Difficulty.Total; index++) {
			if (node.score[index] is null) continue;
			difficulties.Add(new SongDifficultyDto(ApiDifficultyMapper.FromGameDifficulty((Difficulty)index), node.nLevel[index], true));
		}

		IReadOnlyDictionary<string, string> titles = node.ldTitle.GetAllStringsWithLanguageCodes();
		string genre = string.IsNullOrWhiteSpace(node.songGenre) ? "OTHER" : node.songGenre.Trim();
		return new SongDto(
			node.tGetUniqueId(),
			node.ldTitle.GetString(titles.Values.FirstOrDefault() ?? node.tGetUniqueId()),
			titles,
			node.ldSubtitle.GetString(node.ldSubtitle.GetAllStrings().FirstOrDefault() ?? string.Empty),
			genre,
			node.strMaker,
			node.strBreadcrumbs,
			Array.AsReadOnly(difficulties.ToArray()),
			favorite,
			webPreviewAvailable);
	}

	private static string? FindPreviewPath(CSongListNode node) {
		foreach (CScore score in node.score.Where(score => score is not null)) {
			string folder = score.ファイル情報.フォルダの絶対パス;
			foreach (string? fileName in new[] { score.譜面情報.Presound, score.譜面情報.strBGMファイル名 }) {
				if (string.IsNullOrWhiteSpace(fileName)) continue;
				string candidate = Path.IsPathRooted(fileName) ? fileName : Path.Combine(folder ?? string.Empty, fileName);
				if (File.Exists(candidate)) return Path.GetFullPath(candidate);
			}
		}
		return null;
	}

	private static string AudioContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
		".ogg" => "audio/ogg",
		".opus" => "audio/ogg",
		".mp3" => "audio/mpeg",
		".wav" => "audio/wav",
		".flac" => "audio/flac",
		".aac" => "audio/aac",
		".m4a" => "audio/mp4",
		_ => "application/octet-stream",
	};

	private static IEnumerable<SongDifficultyDto> MatchingCharts(SongDto song, ApiDifficulty? difficulty)
		=> difficulty is null ? song.Difficulties : song.Difficulties.Where(chart => chart.Id == difficulty);

	private static string SearchableText(SongDto song)
		=> string.Join('\n', song.Titles.Values.Append(song.Title).Append(song.Subtitle).Append(song.Maker).Append(song.Genre).Append(song.Breadcrumb));
}
