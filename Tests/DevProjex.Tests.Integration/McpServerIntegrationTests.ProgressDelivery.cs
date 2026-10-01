using System.Collections.Concurrent;
using System.Text.Json;
using DevProjex.Tests.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact(Timeout = 60_000)]
	public async Task DelayedEarlierCallbackDoesNotChangeTheObservedProgressSequence()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "File.txt"), "value\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var releaseEarlier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var earlierCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var terminalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var delivery = new ProgressDelivery();
		var endpoints = new ConcurrentQueue<float>();
		var token = new ProgressToken("controlled-delivery");
		await using var registration = server.Client.RegisterNotificationHandler(
			NotificationMethods.ProgressNotification,
			async (notification, cancellationToken) =>
			{
				if (notification.Params?.Deserialize<ProgressNotificationParams>() is not { } value ||
					value.ProgressToken != token)
					return;
				if (value.Progress.Progress == 5f)
					await releaseEarlier.Task.WaitAsync(cancellationToken);
				if (value.Progress.Progress is 5f or 100f)
					endpoints.Enqueue(value.Progress.Progress);
				delivery.Report(value.Progress.Progress);
				if (value.Progress.Progress == 5f)
					earlierCompleted.TrySetResult();
				if (value.Progress.Progress == 100f)
					terminalCompleted.TrySetResult();
			});
		try
		{
			var firstMessage = server.WireMessageCount;
			var result = await server.CallAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "File.txt" },
				options: new RequestOptions { ProgressToken = token });
			await terminalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			Assert.False(earlierCompleted.Task.IsCompleted);
			var messages = server.GetWireMessages(firstMessage);
			var terminalIndex = Array.FindIndex(messages, static message =>
				message.TryGetProperty("method", out var method) &&
				method.GetString() == NotificationMethods.ProgressNotification &&
				message.GetProperty("params").GetProperty("progress").GetSingle() == 100f);
			var resultIndex = Array.FindIndex(messages, static message => message.TryGetProperty("result", out _));
			Assert.InRange(terminalIndex, 0, resultIndex - 1);
			releaseEarlier.TrySetResult();
			await earlierCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			Assert.Equal([100f, 5f], endpoints.ToArray());
			var values = RecordedProgressAssertions.ReadCompletedCall(messages, "controlled-delivery");
			RecordedProgressAssertions.AssertThrottled(values);
			using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			deliveryTimeout.CancelAfter(TimeSpan.FromSeconds(10));
			var delivered = await delivery.WaitForCountAsync(values.Length, deliveryTimeout.Token);
			Assert.Equal(values.Order(), delivered.Order());
		}
		finally
		{
			releaseEarlier.TrySetResult();
		}
	}
}
