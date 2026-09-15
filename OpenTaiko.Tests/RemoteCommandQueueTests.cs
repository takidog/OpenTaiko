using OpenTaiko.RemoteControl;
using System.Text.Json;

namespace OpenTaiko.Tests;

public sealed class RemoteCommandQueueTests {
	[Fact]
	public void DrainExecutesCommandsInOrder() {
		RemoteCommandQueue queue = new();
		RemoteCommandResult first = queue.Enqueue(RemoteCommandType.Preview, new PreviewRequest("one"));
		RemoteCommandResult second = queue.Enqueue(RemoteCommandType.Preview, new PreviewRequest("two"));
		List<string> songIds = new();

		int processed = queue.Drain(command => {
			songIds.Add(command.Payload.Deserialize<PreviewRequest>(RemoteControlJson.Options)!.SongId);
			return RemoteCommandOutcome.Completed;
		});

		Assert.Equal(2, processed);
		Assert.Equal(new[] { "one", "two" }, songIds);
		Assert.True(queue.TryGetResult(first.CommandId, out RemoteCommandResult? firstResult));
		Assert.Equal(RemoteCommandStatus.Completed, firstResult!.Status);
		Assert.True(queue.TryGetResult(second.CommandId, out RemoteCommandResult? secondResult));
		Assert.Equal(RemoteCommandStatus.Completed, secondResult!.Status);
	}

	[Fact]
	public void RejectionAndExceptionDoNotPreventLaterCommands() {
		RemoteCommandQueue queue = new();
		RemoteCommandResult rejected = queue.Enqueue(RemoteCommandType.Play, new PlayRequest("busy", ApiDifficulty.Oni, 1));
		RemoteCommandResult failed = queue.Enqueue(RemoteCommandType.Play, new PlayRequest("throws", ApiDifficulty.Oni, 1));
		RemoteCommandResult completed = queue.Enqueue(RemoteCommandType.StopPreview, new { });

		int processed = queue.Drain(command => command.CommandId switch {
			var id when id == rejected.CommandId => RemoteCommandOutcome.Reject(ApiErrorCodes.GameBusy, "busy"),
			var id when id == failed.CommandId => throw new InvalidOperationException("boom"),
			_ => RemoteCommandOutcome.Completed,
		});

		Assert.Equal(3, processed);
		Assert.True(queue.TryGetResult(rejected.CommandId, out RemoteCommandResult? rejectedResult));
		Assert.Equal(RemoteCommandStatus.Rejected, rejectedResult!.Status);
		Assert.Equal(ApiErrorCodes.GameBusy, rejectedResult.ErrorCode);
		Assert.True(queue.TryGetResult(failed.CommandId, out RemoteCommandResult? failedResult));
		Assert.Equal(RemoteCommandStatus.Failed, failedResult!.Status);
		Assert.True(queue.TryGetResult(completed.CommandId, out RemoteCommandResult? completedResult));
		Assert.Equal(RemoteCommandStatus.Completed, completedResult!.Status);
	}
}
