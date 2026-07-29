using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

internal static partial class TerminalPtyStateAssertions
{
	private static readonly int[] AlternateScreenModes = [47, 1047, 1049];
	private static readonly int[] MouseModes = [1000, 1001, 1002, 1003, 1005, 1006, 1015];

	public static void AssertRestoredBeforeShellMarker(
		string output,
		string screenMode)
	{
		var markerIndex = output.LastIndexOf(
			TerminalPtyHarness.ShellCompletionMarker,
			StringComparison.Ordinal);
		Assert.True(markerIndex >= 0, "The parent shell did not emit its completion marker.");

		var allTransitions = ParseTransitions(output);
		var transitions = allTransitions
			.Where(transition => transition.Index < markerIndex)
			.ToArray();
		var diagnosticContext =
			$"Marker={markerIndex}; complete trace: {DescribeTransitions(allTransitions)}";
		AssertFinalState(
			transitions,
			mode: 25,
			expectedEnabled: true,
			requiredOppositeState: true,
			"The cursor was not visible when the shell resumed.",
			diagnosticContext);
		AssertFinalState(
			transitions,
			mode: 2004,
			expectedEnabled: false,
			requiredOppositeState: true,
			"Bracketed paste was still enabled when the shell resumed.",
			diagnosticContext);

		AssertAlternateScreenState(transitions, screenMode, diagnosticContext);
		AssertMouseState(transitions, diagnosticContext);
	}

	public static bool MatchesKnownTerminalGuiNoMouseInitialization(string output)
	{
		var mouseTransitions = ParseTransitions(output)
			.Where(static transition => MouseModes.Contains(transition.Mode))
			.ToArray();
		var enabledTransitions = mouseTransitions
			.Where(static transition => transition.Enabled)
			.ToArray();
		if (!enabledTransitions.Any(static transition => transition.Mode == 1003) ||
		    !enabledTransitions.Any(static transition => transition.Mode == 1006) ||
		    enabledTransitions.Any(static transition => transition.Mode is not (1003 or 1006 or 1015)))
		{
			return false;
		}
		var disabledTransitions = mouseTransitions
			.Where(static transition => !transition.Enabled)
			.ToArray();
		if (disabledTransitions.Length == 0 ||
		    enabledTransitions.Max(static transition => transition.Index) >=
		    disabledTransitions.Min(static transition => transition.Index))
		{
			return false;
		}

		foreach (var enabledGroup in enabledTransitions.GroupBy(static transition => transition.Mode))
		{
			if (enabledGroup.Count() != 1)
				return false;
			var enableIndex = enabledGroup.Single().Index;
			if (!mouseTransitions.Any(
				    transition =>
					    transition.Mode == enabledGroup.Key &&
					    !transition.Enabled &&
					    transition.Index > enableIndex))
			{
				return false;
			}
		}

		return true;
	}

	private static void AssertAlternateScreenState(
		IReadOnlyList<TerminalModeTransition> transitions,
		string screenMode,
		string diagnosticContext)
	{
		var alternateTransitions = transitions
			.Where(static transition => AlternateScreenModes.Contains(transition.Mode))
			.ToArray();
		if (screenMode.Equals("inline", StringComparison.Ordinal))
		{
			Assert.DoesNotContain(
				alternateTransitions,
				static transition => transition.Enabled);
			return;
		}

		Assert.Contains(
			alternateTransitions,
			static transition => transition.Enabled);
		foreach (var mode in alternateTransitions.Select(static transition => transition.Mode).Distinct())
		{
			AssertFinalState(
				transitions,
				mode,
				expectedEnabled: false,
				requiredOppositeState: false,
				$"Alternate-screen mode {mode} was still enabled when the shell resumed.",
				diagnosticContext);
		}
	}

	private static void AssertMouseState(
		IReadOnlyList<TerminalModeTransition> transitions,
		string diagnosticContext)
	{
		var mouseTransitions = transitions
			.Where(static transition => MouseModes.Contains(transition.Mode))
			.ToArray();
		Assert.Contains(
			mouseTransitions,
			static transition => !transition.Enabled);

		foreach (var mode in mouseTransitions.Select(static transition => transition.Mode).Distinct())
		{
			AssertFinalState(
				transitions,
				mode,
				expectedEnabled: false,
				requiredOppositeState: false,
				$"Mouse mode {mode} was still enabled when the shell resumed.",
				diagnosticContext);
		}
	}

	private static void AssertFinalState(
		IReadOnlyList<TerminalModeTransition> transitions,
		int mode,
		bool expectedEnabled,
		bool requiredOppositeState,
		string failureMessage,
		string diagnosticContext)
	{
		var modeTransitions = transitions
			.Where(transition => transition.Mode == mode)
			.ToArray();
		Assert.NotEmpty(modeTransitions);
		if (requiredOppositeState)
		{
			Assert.Contains(
				modeTransitions,
				transition => transition.Enabled != expectedEnabled);
		}

		var finalTransition = modeTransitions[^1];
		Assert.True(
			finalTransition.Enabled == expectedEnabled,
			$"{failureMessage} Final transition: {finalTransition.Describe()}. " +
			$"Pre-marker trace: {DescribeTransitions(transitions)}. {diagnosticContext}");
	}

	private static IReadOnlyList<TerminalModeTransition> ParseTransitions(
		ReadOnlySpan<char> output)
	{
		var text = output.ToString();
		var transitions = new List<TerminalModeTransition>();
		foreach (Match match in PrivateModeTransitionPattern().Matches(text))
		{
			var enabled = match.Groups["state"].ValueSpan[0] == 'h';
			foreach (var token in match.Groups["modes"].ValueSpan.ToString().Split(';'))
			{
				if (int.TryParse(token, out var mode))
				transitions.Add(new TerminalModeTransition(match.Index, mode, enabled));
			}
		}

		return transitions;
	}

	private static string DescribeTransitions(
		IReadOnlyList<TerminalModeTransition> transitions) =>
		string.Join(", ", transitions.Select(static transition => transition.Describe()));

	[GeneratedRegex(
		"\u001b\\[\\?(?<modes>[0-9]+(?:;[0-9]+)*)(?<state>[hl])",
		RegexOptions.CultureInvariant)]
	private static partial Regex PrivateModeTransitionPattern();

	private sealed record TerminalModeTransition(
		int Index,
		int Mode,
		bool Enabled)
	{
		public string Describe() => $"{Index}:?{Mode}{(Enabled ? 'h' : 'l')}";
	}
}
