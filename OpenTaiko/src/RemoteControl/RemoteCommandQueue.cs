using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTaiko.RemoteControl;

internal enum RemoteCommandType {
	Select,
	Play,
	Preview,
	StopPreview,
	Restart,
	SetFavorite,
	ExitGameplay,
	RetryGameplay,
	ExitResults,
}

internal enum RemoteCommandStatus {
	Pending,
	Running,
	Completed,
	Rejected,
	Failed,
}

internal sealed record RemoteCommand(
	Guid CommandId,
	RemoteCommandType Type,
	JsonElement Payload,
	DateTimeOffset CreatedAtUtc);

internal sealed record RemoteCommandOutcome(
	bool Accepted,
	string? ErrorCode = null,
	string? ErrorMessage = null) {
	public static readonly RemoteCommandOutcome Completed = new(true);

	public static RemoteCommandOutcome Reject(string errorCode, string errorMessage)
		=> new(false, errorCode, errorMessage);
}

internal sealed record RemoteCommandResult(
	Guid CommandId,
	RemoteCommandType Type,
	RemoteCommandStatus Status,
	DateTimeOffset CreatedAtUtc,
	DateTimeOffset UpdatedAtUtc,
	string? ErrorCode = null,
	string? ErrorMessage = null);

/// <summary>
/// The sole write bridge from HTTP handlers to game state. Enqueue may be called
/// from any thread; Drain must only be called by the game loop thread.
/// </summary>
internal sealed class RemoteCommandQueue {
	private readonly ConcurrentQueue<RemoteCommand> pending = new();
	private readonly ConcurrentDictionary<Guid, RemoteCommandResult> results = new();
	private readonly Func<DateTimeOffset> utcNow;
	public event Action<RemoteCommandResult>? CommandChanged;

	public RemoteCommandQueue(Func<DateTimeOffset>? utcNow = null) {
		this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
	}

	public RemoteCommandResult Enqueue<TPayload>(RemoteCommandType type, TPayload payload) {
		DateTimeOffset now = this.utcNow();
		RemoteCommand command = new(
			Guid.NewGuid(),
			type,
			JsonSerializer.SerializeToElement(payload, RemoteControlJson.Options),
			now);
		RemoteCommandResult result = new(
			command.CommandId,
			command.Type,
			RemoteCommandStatus.Pending,
			now,
			now);

		this.results[command.CommandId] = result;
		this.pending.Enqueue(command);
		this.CommandChanged?.Invoke(result);
		return result;
	}

	public bool TryGetResult(Guid commandId, out RemoteCommandResult? result)
		=> this.results.TryGetValue(commandId, out result);

	public IReadOnlyList<RemoteCommandResult> Snapshot()
		=> this.results.Values.OrderByDescending(result => result.CreatedAtUtc).ToArray();

	public int Drain(Func<RemoteCommand, RemoteCommandOutcome> execute, int maximumCommands = 32) {
		ArgumentNullException.ThrowIfNull(execute);
		if (maximumCommands < 1) {
			throw new ArgumentOutOfRangeException(nameof(maximumCommands));
		}

		int processed = 0;
		while (processed < maximumCommands && this.pending.TryDequeue(out RemoteCommand? command)) {
			this.Update(command, RemoteCommandStatus.Running);
			try {
				RemoteCommandOutcome outcome = execute(command);
				this.Update(
					command,
					outcome.Accepted ? RemoteCommandStatus.Completed : RemoteCommandStatus.Rejected,
					outcome.ErrorCode,
					outcome.ErrorMessage);
			} catch (Exception exception) {
				this.Update(command, RemoteCommandStatus.Failed, ApiErrorCodes.InternalError, exception.Message);
			}
			processed++;
		}

		return processed;
	}

	private void Update(
		RemoteCommand command,
		RemoteCommandStatus status,
		string? errorCode = null,
		string? errorMessage = null) {
		RemoteCommandResult result = new(
			command.CommandId,
			command.Type,
			status,
			command.CreatedAtUtc,
			this.utcNow(),
			errorCode,
			errorMessage);
		this.results[command.CommandId] = result;
		this.CommandChanged?.Invoke(result);
	}
}
