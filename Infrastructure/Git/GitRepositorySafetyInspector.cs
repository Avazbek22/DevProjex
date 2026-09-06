using System.ComponentModel;

namespace DevProjex.Infrastructure.Git;

internal sealed record GitRepositorySafetyInspection(
	IReadOnlyList<string> CheckoutFilterDrivers,
	IReadOnlyList<string> UnsafeWorkingTreeDrivers,
	IReadOnlyList<string> ExternalDiffDrivers,
	bool OldGitPromisorRepository,
	bool IsComplete = true)
{
	public static GitRepositorySafetyInspection Unavailable { get; } =
		new([], [], [], OldGitPromisorRepository: true, IsComplete: false);
}

internal static class GitRepositorySafetyInspector
{
	private const int MaximumOutputCharacters = 256 * 1024;

	public static async Task<GitRepositorySafetyInspection> InspectAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		var filterResult = await RunAsync(
			repositoryPath,
			GitProcessOperation.ReadConfigValue(GitConfigReadKind.UnsafeDrivers),
			cancellationToken).ConfigureAwait(false);
		if (filterResult is null)
			return GitRepositorySafetyInspection.Unavailable;

		var checkoutDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var unsafeWorkingDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var diffDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (filterResult.ExitCode == 0)
		{
			foreach (var rawLine in filterResult.Output.Split(
				         ['\r', '\n'],
				         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var key = rawLine.Trim();
				if (TryReadDriver(key, "filter.", out var filterDriver, out var property))
				{
					checkoutDrivers.Add(filterDriver);
					if (property is "clean" or "process")
						unsafeWorkingDrivers.Add(filterDriver);
					continue;
				}
				if (key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase))
					return GitRepositorySafetyInspection.Unavailable;

				if (key.Equals("diff.external", StringComparison.OrdinalIgnoreCase))
				{
					diffDrivers.Add("external");
					continue;
				}
				if (TryReadDriver(key, "diff.", out var diffDriver, out property) &&
				    property is "command" or "textconv")
				{
					diffDrivers.Add(diffDriver);
				}
				else if (key.StartsWith("diff.", StringComparison.OrdinalIgnoreCase))
					return GitRepositorySafetyInspection.Unavailable;
			}
		}

		var oldGitPromisor = false;
		if (!GitRuntime.IsAtLeastVersion(2, 45))
		{
			var promisorResult = await RunAsync(
				repositoryPath,
				GitProcessOperation.ReadConfigValue(GitConfigReadKind.PromisorRemotes),
				cancellationToken).ConfigureAwait(false);
			oldGitPromisor = promisorResult is null ||
			                 promisorResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(promisorResult.Output);
		}

		return new GitRepositorySafetyInspection(
			checkoutDrivers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			unsafeWorkingDrivers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			diffDrivers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			oldGitPromisor);
	}

	public static void TraceDisabledMaterializationFilters(GitRepositorySafetyInspection inspection)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		if (inspection.CheckoutFilterDrivers.Count == 0)
			return;
		Trace.TraceWarning(
			"Git materialization filters were disabled; LFS or similar content remains in pointer form. Drivers: {0}",
			string.Join(", ", inspection.CheckoutFilterDrivers));
	}

	private static bool TryReadDriver(
		string key,
		string prefix,
		out string driver,
		out string property)
	{
		driver = string.Empty;
		property = string.Empty;
		if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			return false;
		var separator = key.LastIndexOf('.');
		if (separator <= prefix.Length || separator >= key.Length - 1)
			return false;
		driver = key[prefix.Length..separator];
		property = key[(separator + 1)..].ToLowerInvariant();
		return driver.Length <= 256 &&
		       driver.All(static character =>
			       char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
	}

	private static async Task<GitInspectionProcessResult?> RunAsync(
		string repositoryPath,
		GitProcessOperation operation,
		CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(operation.Deadline);
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryPath, operation)
		};
		try
		{
			if (!process.Start())
				return null;
			process.StandardInput.Close();
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return null;
		}

		var output = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			MaximumOutputCharacters,
			deadline.Token);
		var error = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			MaximumOutputCharacters,
			deadline.Token);
		try
		{
			await GitRepositoryService.WaitForExitOrTerminateAsync(process, deadline.Token)
				.ConfigureAwait(false);
			if (!await GitProcessOutputReader
				    .WaitForCompletionAfterExitAsync(process, output, error)
				    .ConfigureAwait(false))
			{
				return null;
			}
			var standardOutput = await output.ConfigureAwait(false);
			var standardError = await error.ConfigureAwait(false);
			return standardOutput.ExceededLimit || standardError.ExceededLimit
				? null
				: new GitInspectionProcessResult(process.ExitCode, standardOutput.Text);
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader.ObserveAfterTerminationAsync(process, output, error)
				.ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested)
				throw;
			return null;
		}
	}

	private sealed record GitInspectionProcessResult(int ExitCode, string Output);
}
