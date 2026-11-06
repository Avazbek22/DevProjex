namespace DevProjex.Tests.Terminal;

internal sealed class TerminalSizeResponseAcknowledgement(int columns, int rows)
{
	private readonly string _expectedResponse = $"\u001b[8;{rows};{columns}t";
	private readonly TaskCompletionSource _completion =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task Completion => _completion.Task;

	public void ObserveWrittenResponse(string response)
	{
		if (string.Equals(response, _expectedResponse, StringComparison.Ordinal))
			_completion.TrySetResult();
	}
}
