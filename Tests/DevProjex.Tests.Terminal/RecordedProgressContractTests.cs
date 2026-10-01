using System.Text.Json;
using DevProjex.Tests.Mcp;

namespace DevProjex.Tests.Terminal;

public sealed class RecordedProgressContractTests
{
	[Fact]
	public void OrderedNotificationsAreScopedToTheRequestedToken()
	{
		var messages = RecordedProgressAssertions.Parse(string.Join('\n',
			Notification(5), Notification(100, "other"), Notification(100), Result));
		var values = RecordedProgressAssertions.ReadCompletedCall(messages, "throttle");
		Assert.Equal([5f, 100f], values);
		RecordedProgressAssertions.AssertThrottled(values);
	}

	[Fact]
	public void NumericTokensFromAnotherCallDoNotContributeToTheCount()
	{
		const string other = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":42,"progress":100,"total":100}}""";
		var messages = RecordedProgressAssertions.Parse(string.Join('\n', Notification(5), other, Notification(100), Result));
		var values = RecordedProgressAssertions.ReadCompletedCall(messages, "throttle");
		Assert.Equal([5f, 100f], values);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(21)]
	public void NotificationCountsOutsideThePublishedThrottleAreRejected(int count)
	{
		var values = Enumerable.Range(0, count).Select(index =>
			index == count - 1 ? 100f : 5f + index).ToArray();
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.AssertThrottled(values));
	}

	[Theory]
	[InlineData(1f, 100f)]
	[InlineData(5f, 99f)]
	public void IncorrectEndpointsAreRejected(float first, float last) =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			RecordedProgressAssertions.AssertThrottled([first, last]));

	[Fact]
	public void ACallbackCompletionOrderCannotStandInForTransportOrder() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			RecordedProgressAssertions.AssertThrottled([100f, 5f]));

	[Fact]
	public void ANonIncreasingTransportSequenceIsRejected() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			RecordedProgressAssertions.AssertThrottled([5f, 50f, 20f, 100f]));

	[Fact]
	public void AProgressNotificationAfterTheResultIsRejected() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.ReadCompletedCall(
			RecordedProgressAssertions.Parse(string.Join('\n', Notification(5), Result, Notification(100))),
			"throttle"));

	[Fact]
	public void ATruncatedSequenceWithoutATerminalNotificationIsRejected() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.ReadCompletedCall(
			RecordedProgressAssertions.Parse(string.Join('\n', Notification(5), Result)), "throttle"));

	[Fact]
	public void AnUnfinishedCallIsRejected() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.ReadCompletedCall(
			RecordedProgressAssertions.Parse(string.Join('\n', Notification(5), Notification(100))), "throttle"));

	[Fact]
	public void MultipleCallResultsCannotBeUsedAsOneCompletedCall() =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.ReadCompletedCall(
			RecordedProgressAssertions.Parse(string.Join('\n', Notification(5), Notification(100), Result, Result)),
			"throttle"));

	[Theory]
	[InlineData(99f)]
	[InlineData(101f)]
	public void AnIncorrectProgressTotalIsRejected(float total) =>
		Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RecordedProgressAssertions.ReadCompletedCall(
			RecordedProgressAssertions.Parse(string.Join('\n', Notification(5, total: total), Notification(100), Result)),
			"throttle"));

	[Fact]
	public async Task DeliveryWaitsForEveryRecordedNotificationEvenWhenTheTerminalOneArrivesFirst()
	{
		var delivery = new ProgressDelivery();
		delivery.Report(100f);
		var completed = delivery.WaitForCountAsync(2, TestContext.Current.CancellationToken);
		Assert.False(completed.IsCompleted);
		delivery.Report(5f);
		var values = await completed;
		Assert.Equal([100f, 5f], values);
	}

	[Fact]
	public async Task DeliveryWaitObservesAlreadyDeliveredValuesAndCancellation()
	{
		var delivery = new ProgressDelivery();
		delivery.Report(5f);
		var values = await delivery.WaitForCountAsync(1, TestContext.Current.CancellationToken);
		Assert.Equal([5f], values);
		using var cancellation = new CancellationTokenSource();
		var waiting = delivery.WaitForCountAsync(2, cancellation.Token);
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
	}

	private static string Notification(float value, string token = "throttle", float total = 100f) =>
		JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "notifications/progress",
			@params = new { progressToken = token, progress = value, total }
		});

	private const string Result = """{"jsonrpc":"2.0","id":2,"result":{"content":[]}}""";
}
