namespace OpenTaiko.RemoteControl;

internal sealed class GameRemoteControlApi : IRemoteControlApi {
	private readonly string version;
	private readonly RemoteCommandQueue commands;
	private readonly PlayHistoryService history;
	private SongCatalogSnapshot catalog = SongCatalogSnapshot.Empty;
	private GameStateDto state = new("startup", null, null, 1);
	private bool songIndexReady;

	public GameRemoteControlApi(string version, RemoteCommandQueue commands, PlayHistoryService history) {
		this.version = version;
		this.commands = commands;
		this.history = history;
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
	public IReadOnlyList<PlayHistoryEntryDto> GetHistory(int limit) => this.history.GetRecent(limit);
	public bool TryGetCommand(Guid commandId, out RemoteCommandResult? result) => this.commands.TryGetResult(commandId, out result);

	public RemoteCommandResult Enqueue<TPayload>(RemoteCommandType type, TPayload payload) {
		if (!this.GetState().Stage.Equals("SongSelect", StringComparison.OrdinalIgnoreCase)) {
			throw new ApiRouteException(409, ApiErrorCodes.GameBusy, "OpenTaiko is not currently at song selection.");
		}
		this.Validate(type, payload);
		return this.commands.Enqueue(type, payload);
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
