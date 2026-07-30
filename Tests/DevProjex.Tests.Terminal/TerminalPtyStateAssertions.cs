using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

internal static partial class TerminalPtyStateAssertions
{
	private static readonly int[] AlternateScreenModes = [47, 1047, 1049];
	private static readonly int[] MouseModes = [1000, 1001, 1002, 1003, 1005, 1006, 1015];

	public static void AssertRestoredAtShellCompletion(
		string output,
		string screenMode)
	{
		var markerIndex = output.LastIndexOf(
			TerminalPtyHarness.ShellCompletionMarker,
			StringComparison.Ordinal);
		Assert.True(markerIndex >= 0, "The parent shell did not emit its completion marker.");
		if (!OperatingSystem.IsWindows())
			AssertUnixTerminalStateRestored(output, markerIndex);

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
		AssertSgrStateRestored(output, markerIndex, diagnosticContext);
	}

	internal static string? FindUnixTerminalStateMismatch(
		string output,
		int markerIndex)
	{
		ArgumentNullException.ThrowIfNull(output);
		if (markerIndex < 0 || markerIndex > output.Length)
			throw new ArgumentOutOfRangeException(nameof(markerIndex));

		var beforeShellCompletion = output.AsSpan(0, markerIndex);
		var mismatchIndex = beforeShellCompletion.LastIndexOf(
			TerminalPtyHarness.ShellTerminalStateMismatchMarker,
			StringComparison.Ordinal);
		if (mismatchIndex < 0)
			return null;

		var mismatch = beforeShellCompletion[mismatchIndex..];
		var lineEnd = mismatch.IndexOfAny('\r', '\n');
		return (lineEnd < 0 ? mismatch : mismatch[..lineEnd]).ToString();
	}

	private static void AssertUnixTerminalStateRestored(
		string output,
		int markerIndex)
	{
		var shellOutput = output[..markerIndex];
		Assert.Contains(
			TerminalPtyHarness.ShellTerminalPropertiesRestoredMarker,
			shellOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			TerminalPtyHarness.ShellLineInputAcceptedMarker,
			shellOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			TerminalPtyHarness.ShellUsabilityVerifiedMarker,
			shellOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			TerminalPtyHarness.ShellEchoProbe,
			shellOutput,
			StringComparison.Ordinal);
		Assert.Contains(
			TerminalPtyHarness.ShellSettledTerminalStateRestoredMarker,
			shellOutput,
			StringComparison.Ordinal);
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
		Assert.True(
			modeTransitions.Length > 0,
			$"{failureMessage} No transitions for mode {mode}. {diagnosticContext}");
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

	private static void AssertSgrStateRestored(
		string output,
		int markerIndex,
		string diagnosticContext)
	{
		var state = new SgrState();
		foreach (Match match in SgrPattern().Matches(output[..markerIndex]))
		{
			var parameters = match.Groups["parameters"].Value;
			var codes = string.IsNullOrEmpty(parameters)
				? [0]
				: parameters
					.Split(';', StringSplitOptions.None)
					.Select(static token => int.TryParse(token, out var code) ? code : 0)
					.ToArray();
			for (var index = 0; index < codes.Length; index++)
			{
				var code = codes[index];
				switch (code)
				{
					case 0:
						state = new SgrState();
						break;
					case 1:
					case 2:
						state.BoldOrDim = true;
						break;
					case 3:
						state.Italic = true;
						break;
					case 4:
						state.Underline = true;
						break;
					case 5:
					case 6:
						state.Blink = true;
						break;
					case 7:
						state.Inverse = true;
						break;
					case 8:
						state.Conceal = true;
						break;
					case 9:
						state.Strike = true;
						break;
					case 22:
						state.BoldOrDim = false;
						break;
					case 23:
						state.Italic = false;
						break;
					case 24:
						state.Underline = false;
						break;
					case 25:
						state.Blink = false;
						break;
					case 27:
						state.Inverse = false;
						break;
					case 28:
						state.Conceal = false;
						break;
					case 29:
						state.Strike = false;
						break;
					case >= 30 and <= 37:
					case >= 90 and <= 97:
						state.ForegroundDefault = false;
						break;
					case 38:
						state.ForegroundDefault = false;
						index += ExtendedColorParameterCount(codes, index);
						break;
					case 39:
						state.ForegroundDefault = true;
						break;
					case >= 40 and <= 47:
					case >= 100 and <= 107:
						state.BackgroundDefault = false;
						break;
					case 48:
						state.BackgroundDefault = false;
						index += ExtendedColorParameterCount(codes, index);
						break;
					case 49:
						state.BackgroundDefault = true;
						break;
				}
			}
		}

		Assert.True(
			state is
			{
				DecorationActive: false,
				ForegroundDefault: true,
				BackgroundDefault: true
			},
			$"SGR color/style state was not reset before the shell resumed. " +
			$"State={state}. {diagnosticContext}");
	}

	private static int ExtendedColorParameterCount(
		IReadOnlyList<int> codes,
		int colorCodeIndex)
	{
		if (colorCodeIndex + 1 >= codes.Count)
			return 0;

		return codes[colorCodeIndex + 1] switch
		{
			5 when colorCodeIndex + 2 < codes.Count => 2,
			2 when colorCodeIndex + 4 < codes.Count => 4,
			_ => 0
		};
	}

	private static string DescribeTransitions(
		IReadOnlyList<TerminalModeTransition> transitions) =>
		string.Join(", ", transitions.Select(static transition => transition.Describe()));

	[GeneratedRegex(
		"\u001b\\[\\?(?<modes>[0-9]+(?:;[0-9]+)*)(?<state>[hl])",
		RegexOptions.CultureInvariant)]
	private static partial Regex PrivateModeTransitionPattern();

	[GeneratedRegex(
		"\u001b\\[(?<parameters>[0-9;]*)m",
		RegexOptions.CultureInvariant)]
	private static partial Regex SgrPattern();

	private sealed class SgrState
	{
		public bool BoldOrDim { get; set; }
		public bool Italic { get; set; }
		public bool Underline { get; set; }
		public bool Blink { get; set; }
		public bool Inverse { get; set; }
		public bool Conceal { get; set; }
		public bool Strike { get; set; }
		public bool DecorationActive =>
			BoldOrDim || Italic || Underline || Blink || Inverse || Conceal || Strike;
		public bool ForegroundDefault { get; set; } = true;
		public bool BackgroundDefault { get; set; } = true;

		public override string ToString() =>
			$"DecorationActive={DecorationActive}, " +
			$"ForegroundDefault={ForegroundDefault}, " +
			$"BackgroundDefault={BackgroundDefault}";
	}

	private sealed record TerminalModeTransition(
		int Index,
		int Mode,
		bool Enabled)
	{
		public string Describe() => $"{Index}:?{Mode}{(Enabled ? 'h' : 'l')}";
	}
}
