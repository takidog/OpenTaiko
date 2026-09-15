using System.Collections.ObjectModel;

namespace OpenTaiko.RemoteControl;

/// <summary>
/// An immutable, path-free copy of the song catalog for use by HTTP threads.
/// </summary>
internal sealed class SongCatalogSnapshot {
	private readonly SongDto[] songs;
	private readonly IReadOnlyDictionary<string, SongDto> songsById;

	public static readonly SongCatalogSnapshot Empty = new(Array.Empty<SongDto>());

	public SongCatalogSnapshot(IEnumerable<SongDto> songs) {
		this.songs = songs.OrderBy(song => song.Title, StringComparer.OrdinalIgnoreCase).ToArray();
		this.songsById = new ReadOnlyDictionary<string, SongDto>(
			this.songs.ToDictionary(song => song.Id, StringComparer.Ordinal));
	}

	public int Count => this.songs.Length;

	public static SongCatalogSnapshot FromSongNodes(
		IEnumerable<CSongListNode> nodes,
		Func<string, bool>? isFavorite = null) {
		isFavorite ??= _ => false;
		IEnumerable<SongDto> dtos = nodes
			.Where(node => node.nodeType == CSongListNode.ENodeType.SCORE && !string.IsNullOrWhiteSpace(node.tGetUniqueId()))
			.Select(node => FromSongNode(node, isFavorite(node.tGetUniqueId())));
		return new SongCatalogSnapshot(dtos);
	}

	public SongDto? GetSong(string songId)
		=> this.songsById.TryGetValue(songId, out SongDto? song) ? song : null;

	public SongPageDto Search(SongQuery query) {
		IEnumerable<SongDto> filtered = this.songs;
		if (!string.IsNullOrWhiteSpace(query.Query)) {
			string text = query.Query.Trim();
			filtered = filtered.Where(song => SearchableText(song).Contains(text, StringComparison.OrdinalIgnoreCase));
		}
		if (!string.IsNullOrWhiteSpace(query.Genre)) {
			filtered = filtered.Where(song => song.Genre.Equals(query.Genre, StringComparison.OrdinalIgnoreCase));
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

	private static SongDto FromSongNode(CSongListNode node, bool favorite) {
		List<SongDifficultyDto> difficulties = new();
		for (int index = 0; index < (int)Difficulty.Total; index++) {
			if (node.score[index] is null) continue;
			difficulties.Add(new SongDifficultyDto(ToApiDifficulty((Difficulty)index), node.nLevel[index], true));
		}

		IReadOnlyDictionary<string, string> titles = node.ldTitle.GetAllStringsWithLanguageCodes();
		return new SongDto(
			node.tGetUniqueId(),
			node.ldTitle.GetString(titles.Values.FirstOrDefault() ?? node.tGetUniqueId()),
			titles,
			node.ldSubtitle.GetString(node.ldSubtitle.GetAllStrings().FirstOrDefault() ?? string.Empty),
			node.songGenre,
			node.strMaker,
			node.strBreadcrumbs,
			Array.AsReadOnly(difficulties.ToArray()),
			favorite);
	}

	private static ApiDifficulty ToApiDifficulty(Difficulty difficulty) => difficulty switch {
		Difficulty.Easy => ApiDifficulty.Easy,
		Difficulty.Normal => ApiDifficulty.Normal,
		Difficulty.Hard => ApiDifficulty.Hard,
		Difficulty.Oni => ApiDifficulty.Oni,
		Difficulty.Edit => ApiDifficulty.Ura,
		Difficulty.Tower => ApiDifficulty.Tower,
		Difficulty.Dan => ApiDifficulty.Dan,
		_ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
	};

	private static IEnumerable<SongDifficultyDto> MatchingCharts(SongDto song, ApiDifficulty? difficulty)
		=> difficulty is null ? song.Difficulties : song.Difficulties.Where(chart => chart.Id == difficulty);

	private static string SearchableText(SongDto song)
		=> string.Join('\n', song.Titles.Values.Append(song.Title).Append(song.Subtitle).Append(song.Maker).Append(song.Genre).Append(song.Breadcrumb));
}
