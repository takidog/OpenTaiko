using System.Text.Json;

namespace OpenTaiko.RemoteControl;

/// <summary>
/// Executes remote selection commands on the game loop thread.
/// </summary>
internal sealed class SongSelectionService {
	private readonly GameRemoteControlApi api;

	public SongSelectionService(GameRemoteControlApi api) {
		this.api = api;
	}

	public RemoteCommandOutcome Execute(RemoteCommand command) {
		if (command.Type is RemoteCommandType.ExitGameplay or RemoteCommandType.RetryGameplay) {
			return this.ControlGameplay(command.Type);
		}

		if (OpenTaiko.rCurrentStage?.eStageID == CStage.EStage.Results
			&& command.Type is RemoteCommandType.Select or RemoteCommandType.Play or RemoteCommandType.Restart) {
			if (!OpenTaiko.app.TryLeaveResultsForRemoteControl()) {
				return RemoteCommandOutcome.Reject(ApiErrorCodes.GameBusy, "The results screen is not ready to leave.");
			}
		}

		if (OpenTaiko.rCurrentStage?.eStageID != CStage.EStage.SongSelect
			|| OpenTaiko.stageSongSelect.ePhaseID != CStage.EPhase.Common_NORMAL) {
			return RemoteCommandOutcome.Reject(ApiErrorCodes.GameBusy, "Commands are only accepted while song selection is idle.");
		}

		return command.Type switch {
			RemoteCommandType.Select => this.Select(command.Payload.Deserialize<SelectionRequest>(RemoteControlJson.Options)!),
			RemoteCommandType.Play => this.Play(command.Payload.Deserialize<PlayRequest>(RemoteControlJson.Options)!),
			RemoteCommandType.Preview => this.Preview(command.Payload.Deserialize<PreviewRequest>(RemoteControlJson.Options)!),
			RemoteCommandType.StopPreview => this.StopPreview(),
			RemoteCommandType.Restart => this.Restart(command.Payload.Deserialize<RestartRequest>(RemoteControlJson.Options)!),
			RemoteCommandType.SetFavorite => this.SetFavorite(command.Payload.Deserialize<FavoriteRequest>(RemoteControlJson.Options)!),
			_ => RemoteCommandOutcome.Reject(ApiErrorCodes.InvalidRequest, "Unknown command type."),
		};
	}

	private RemoteCommandOutcome ControlGameplay(RemoteCommandType type) {
		if (OpenTaiko.rCurrentStage?.eStageID != CStage.EStage.Game
			|| OpenTaiko.stageGameScreen.ePhaseID != CStage.EPhase.Common_NORMAL) {
			return RemoteCommandOutcome.Reject(ApiErrorCodes.GameBusy, "Gameplay controls are not available right now.");
		}

		if (type == RemoteCommandType.RetryGameplay) {
			OpenTaiko.stageGameScreen.t演奏やりなおし();
		} else {
			OpenTaiko.AbortPlayHistory();
			OpenTaiko.stageGameScreen.t演奏中止();
		}
		return RemoteCommandOutcome.Completed;
	}

	private RemoteCommandOutcome Select(SelectionRequest request) {
		return this.SelectSong(request.SongId, request.Difficulty)
			? RemoteCommandOutcome.Completed
			: RemoteCommandOutcome.Reject(ApiErrorCodes.SongNotFound, "The song is no longer available.");
	}

	private RemoteCommandOutcome Preview(PreviewRequest request) {
		SongDto? song = OpenTaiko.RemoteControlApi?.GetSong(request.SongId);
		ApiDifficulty difficulty = song?.Difficulties.FirstOrDefault(chart => chart.Available)?.Id ?? ApiDifficulty.Oni;
		return this.SelectSong(request.SongId, difficulty)
			? RemoteCommandOutcome.Completed
			: RemoteCommandOutcome.Reject(ApiErrorCodes.SongNotFound, "The song is no longer available.");
	}

	private RemoteCommandOutcome StopPreview() {
		OpenTaiko.stageSongSelect.actPresound.tStopSound();
		CSongSelectSongManager.enable();
		CSongSelectSongManager.playSongIfPossible();
		return RemoteCommandOutcome.Completed;
	}

	private RemoteCommandOutcome Play(PlayRequest request) {
		if (!this.SelectSong(request.SongId, request.Difficulty)) {
			return RemoteCommandOutcome.Reject(ApiErrorCodes.SongNotFound, "The song is no longer available.");
		}
		if (request.PlayerCount is int playerCount) OpenTaiko.ConfigIni.nPlayerCount = playerCount;
		int difficulty = (int)ApiDifficultyMapper.ToGameDifficulty(request.Difficulty);
		for (int player = 0; player < OpenTaiko.ConfigIni.nPlayerCount; player++) {
			OpenTaiko.stageSongSelect.t曲を選択する(difficulty, player);
		}
		return RemoteCommandOutcome.Completed;
	}

	private RemoteCommandOutcome Restart(RestartRequest request) {
		PlayHistoryEntryDto? entry = request.HistoryId is Guid historyId
			? this.api.History.Get(historyId)
			: this.api.History.GetLatest();
		if (entry is null) {
			return RemoteCommandOutcome.Reject(ApiErrorCodes.HistoryNotFound, "The play history entry is no longer available.");
		}

		OpenTaiko.SaveFile = entry.SaveSlot - 1;
		OpenTaiko.PlayerSide = entry.PlayerSide == "right" ? 1 : 0;
		return this.Play(new PlayRequest(entry.SongId, entry.Difficulty, entry.PlayerCount));
	}

	private RemoteCommandOutcome SetFavorite(FavoriteRequest request) {
		bool current = OpenTaiko.Favorites.tIsFavorite(request.SongId);
		if (current != request.Favorite) OpenTaiko.Favorites.tToggleFavorite(request.SongId);
		this.api.SetFavorite(request.SongId, request.Favorite);
		return RemoteCommandOutcome.Completed;
	}

	private bool SelectSong(string songId, ApiDifficulty apiDifficulty) {
		CSongListNode? indexedSong = CSongDict.tGetNodeFromID(songId);
		if (indexedSong is null) return false;

		int difficulty = (int)ApiDifficultyMapper.ToGameDifficulty(apiDifficulty);
		if (indexedSong.score[difficulty] is null) return false;

		// Refresh resolves the cloned dictionary node back to the live node held by the
		// song-select UI and reconstructs the opened folder path and bar positions.
		OpenTaiko.SongMount.rCurrentlySelectedSong = indexedSong;
		OpenTaiko.stageSongSelect.Refresh(OpenTaiko.Songs管理, bRemakeSongTitleBar: true);
		CSongListNode? selected = OpenTaiko.SongMount.rCurrentlySelectedSong;
		if (selected?.tGetUniqueId() != songId || selected.score[difficulty] is null) return false;

		OpenTaiko.SongMount.nCurrentAnchorDifficulty = difficulty;
		OpenTaiko.SongMount.nCurrentSongDifficulty = difficulty;
		OpenTaiko.stageSongSelect.actSongList.t現在選択中の曲を元に曲バーを再構成する();
		OpenTaiko.stageSongSelect.actSongList.t選択曲が変更された(true);
		OpenTaiko.stageSongSelect.actSongList.tUpdateCurSong();
		OpenTaiko.stageSongSelect.actSongList.tResetTitleKey();
		OpenTaiko.stageSongSelect.actSongList.tバーの初期化();
		OpenTaiko.stageSongSelect.tNotifySelectedSongChange();
		return true;
	}
}
