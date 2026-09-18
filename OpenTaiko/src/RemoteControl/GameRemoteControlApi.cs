namespace OpenTaiko.RemoteControl;

internal sealed class GameRemoteControlApi : IRemoteControlApi {
	private readonly string version;
	private readonly RemoteCommandQueue commands;
	private readonly PlayHistoryService history;
	private readonly PlaylistService playlist;
	private SongCatalogSnapshot catalog = SongCatalogSnapshot.Empty;
	private GameStateDto state = new("startup", null, null, 1);
	private bool songIndexReady;

	public GameRemoteControlApi(
		string version,
		RemoteCommandQueue commands,
		PlayHistoryService history,
		PlaylistService? playlist = null) {
		this.version = version;
		this.commands = commands;
		this.history = history;
		this.playlist = playlist ?? new PlaylistService();
	}

	public RemoteCommandQueue Commands => this.commands;
	public PlayHistoryService History => this.history;

	public void ReplaceCatalog(SongCatalogSnapshot catalog, bool ready = true) {
		ArgumentNullException.ThrowIfNull(catalog);
		Interlocked.Exchange(ref this.catalog, catalog);
		Volatile.Write(ref this.songIndexReady, ready);
	}

	public void UpdateState(GameStateDto state) {
		ArgumentNullException.ThrowIfNull(state);
		Interlocked.Exchange(ref this.state, state);
	}

	public HealthDto GetHealth() => new("ok", this.version, Volatile.Read(ref this.songIndexReady));
	public GameStateDto GetState() => Volatile.Read(ref this.state);
	public SongPageDto GetSongs(SongQuery query) => Volatile.Read(ref this.catalog).Search(query);
	public SongDto? GetSong(string songId) => Volatile.Read(ref this.catalog).GetSong(songId);
	public SongAudioDto? GetSongAudio(string songId) => Volatile.Read(ref this.catalog).GetPreviewAudio(songId);
	public IReadOnlyList<string> GetGenres() => Volatile.Read(ref this.catalog).GetGenres();
	public IReadOnlyList<PlaylistDto> GetPlaylists()
		=> this.playlist.GetAll().Select(this.ToPlaylistDto).ToArray();
	public PlaylistExportDto ExportPlaylists() => this.playlist.Export();
	public PlaylistDto CreatePlaylist(string name) => this.ToPlaylistDto(this.playlist.Create(name));
	public PlaylistDto RenamePlaylist(Guid playlistId, string name) => this.ToPlaylistDto(this.playlist.Rename(playlistId, name));
	public void DeletePlaylist(Guid playlistId) => this.playlist.Delete(playlistId);
	public PlaylistDto AddSongToPlaylist(Guid playlistId, string songId) {
		ValidateSong(Volatile.Read(ref this.catalog), songId);
		return this.ToPlaylistDto(this.playlist.AddSong(playlistId, songId));
	}
	public PlaylistDto RemoveSongFromPlaylist(Guid playlistId, string songId)
		=> this.ToPlaylistDto(this.playlist.RemoveSong(playlistId, songId));
	public PlaylistDto ReorderPlaylist(Guid playlistId, IReadOnlyList<string> songIds)
		=> this.ToPlaylistDto(this.playlist.ReorderSongs(playlistId, songIds));
	public void ImportPlaylists(PlaylistExportDto document) {
		ArgumentNullException.ThrowIfNull(document);
		if (document.Playlists is null) throw new ArgumentException("playlists is required.");
		SongCatalogSnapshot currentCatalog = Volatile.Read(ref this.catalog);
		PlaylistDefinitionDto[] sanitized = document.Playlists.Select(item => item with {
			SongIds = item.SongIds.Where(songId => currentCatalog.GetSong(songId) is not null).ToArray(),
		}).ToArray();
		this.playlist.Import(document with { Playlists = sanitized });
	}
	public IReadOnlyList<PlayHistoryEntryDto> GetHistory(int limit) {
		SongCatalogSnapshot currentCatalog = Volatile.Read(ref this.catalog);
		return this.history.GetRecent(limit).Select(entry => {
			SongDto? song = currentCatalog.GetSong(entry.SongId);
			return song is null ? entry : entry with {
				SongTitle = string.IsNullOrWhiteSpace(entry.SongTitle) ? song.Title : entry.SongTitle,
				Genre = string.IsNullOrWhiteSpace(entry.Genre) ? song.Genre : entry.Genre,
			};
		}).ToArray();
	}
	public bool TryGetCommand(Guid commandId, out RemoteCommandResult? result) => this.commands.TryGetResult(commandId, out result);

	public RemoteCommandResult Enqueue<TPayload>(RemoteCommandType type, TPayload payload) {
		GameStateDto currentState = this.GetState();
		bool stageAllowed = type switch {
			RemoteCommandType.Select or RemoteCommandType.Play or RemoteCommandType.Restart
				=> IsStage(currentState, "SongSelect") || IsStage(currentState, "Results"),
			RemoteCommandType.Preview
				=> IsStage(currentState, "SongSelect") || IsStage(currentState, "Results"),
			RemoteCommandType.SetFavorite => true,
			RemoteCommandType.ExitGameplay => IsStage(currentState, "Game") && currentState.CanExit,
			RemoteCommandType.RetryGameplay => IsStage(currentState, "Game") && currentState.CanRetry,
			RemoteCommandType.ExitResults => IsStage(currentState, "Results"),
			_ => IsStage(currentState, "SongSelect"),
		};
		if (!stageAllowed) {
			throw new ApiRouteException(409, ApiErrorCodes.GameBusy, "The command is not available in the current OpenTaiko stage.");
		}
		this.Validate(type, payload);
		return this.commands.Enqueue(type, payload);
	}

	private static bool IsStage(GameStateDto state, string stage)
		=> state.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase);

	public void SetFavorite(string songId, bool favorite) {
		while (true) {
			SongCatalogSnapshot current = Volatile.Read(ref this.catalog);
			SongCatalogSnapshot updated = current.WithFavorite(songId, favorite);
			if (ReferenceEquals(current, updated)
				|| ReferenceEquals(Interlocked.CompareExchange(ref this.catalog, updated, current), current)) return;
		}
	}

	private PlaylistDto ToPlaylistDto(PlaylistDefinitionDto playlist) {
		SongCatalogSnapshot currentCatalog = Volatile.Read(ref this.catalog);
		return new PlaylistDto(
			playlist.Id,
			playlist.Name,
			playlist.CreatedAtUtc,
			playlist.UpdatedAtUtc,
			currentCatalog.GetSongs(playlist.SongIds));
	}

	private void Validate<TPayload>(RemoteCommandType type, TPayload payload) {
		SongCatalogSnapshot currentCatalog = Volatile.Read(ref this.catalog);
		switch (type, payload) {
			case (RemoteCommandType.Select, SelectionRequest selection):
				ValidateChart(currentCatalog, selection.SongId, selection.Difficulty);
				break;
			case (RemoteCommandType.Play, PlayRequest play):
				ValidateChart(currentCatalog, play.SongId, play.Difficulty);
				break;
			case (RemoteCommandType.Preview, PreviewRequest preview):
				ValidateSong(currentCatalog, preview.SongId);
				break;
			case (RemoteCommandType.SetFavorite, FavoriteRequest favorite):
				ValidateSong(currentCatalog, favorite.SongId);
				break;
			case (RemoteCommandType.Restart, RestartRequest restart): {
				PlayHistoryEntryDto? entry = restart.HistoryId is Guid historyId
					? this.history.Get(historyId)
					: this.history.GetLatest();
				if (entry is null) {
					throw new ApiRouteException(404, ApiErrorCodes.HistoryNotFound, "The requested play history entry was not found.");
				}
				ValidateChart(currentCatalog, entry.SongId, entry.Difficulty);
				break;
			}
		}
	}

	private static SongDto ValidateSong(SongCatalogSnapshot catalog, string songId) {
		return catalog.GetSong(songId)
			?? throw new ApiRouteException(404, ApiErrorCodes.SongNotFound, "The requested song was not found.");
	}

	private static void ValidateChart(SongCatalogSnapshot catalog, string songId, ApiDifficulty difficulty) {
		SongDto song = ValidateSong(catalog, songId);
		if (!song.Difficulties.Any(chart => chart.Id == difficulty && chart.Available)) {
			throw new ApiRouteException(
				422,
				ApiErrorCodes.InvalidDifficulty,
				"The selected chart does not contain the requested difficulty.");
		}
	}
}
