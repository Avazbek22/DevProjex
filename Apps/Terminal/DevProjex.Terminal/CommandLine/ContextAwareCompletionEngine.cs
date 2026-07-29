using System.CommandLine;
using System.CommandLine.Completions;

namespace DevProjex.Terminal.CommandLine;

internal static class ContextAwareCompletionEngine
{
	public static IReadOnlyList<string> Complete(
		RootCommand root,
		string commandLine,
		int cursorPosition)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(commandLine);
		var normalized = NormalizeInvocation(commandLine, cursorPosition);
		if (normalized is null)
			return [];

		var parseResult = root.Parse(normalized.Value.CommandLine);
		var command = parseResult.CommandResult.Command;
		var options = root.Options
			.Where(static option => option.Recursive)
			.Concat(command.Options)
			.Where(static option => !option.Hidden)
			.Distinct()
			.ToArray();
		var optionByToken = options
			.SelectMany(option => new[] { option.Name }
				.Concat(option.Aliases)
				.Select(token => (Token: token, Option: option)))
			.ToDictionary(static item => item.Token, static item => item.Option, StringComparer.Ordinal);
		var visibleCommands = command.Subcommands
			.Where(static child => !child.Hidden)
			.Select(static child => child.Name)
			.ToHashSet(StringComparer.Ordinal);
		var completionContext = parseResult.GetCompletionContext();
		var wordToComplete = completionContext.WordToComplete;
		var hasCompletedDelimiter = HasCompletedDelimiter(
			normalized.Value.CommandLine,
			normalized.Value.CursorPosition);
		var valueOption = hasCompletedDelimiter
			? null
			: ResolveValueOptionContext(
				optionByToken,
				normalized.Value.CommandLine,
				normalized.Value.CursorPosition,
				wordToComplete);
		var choiceOption = valueOption is not null &&
		                   IsChoiceValueType(valueOption.ValueType)
			? valueOption
			: null;
		var choiceArgument = choiceOption is null
			? ResolveChoiceArgumentContext(
				parseResult,
				command,
				normalized.Value.CommandLine,
				normalized.Value.CursorPosition,
				wordToComplete)
			: null;
		var dataArgument = ResolveDataArgumentContext(
			parseResult,
			command,
			normalized.Value.CommandLine,
			normalized.Value.CursorPosition,
			wordToComplete,
			hasCompletedDelimiter);
		var acceptsDataCandidates = valueOption is not null ||
		                            choiceArgument is not null ||
		                            dataArgument is not null;
		var completionItems = choiceOption is not null
			? choiceOption.GetCompletions(CompletionContext.Empty)
			: choiceArgument is not null
				? choiceArgument.GetCompletions(CompletionContext.Empty)
				: dataArgument is not null
					? dataArgument.GetCompletions(completionContext)
					: parseResult.GetCompletions(normalized.Value.CursorPosition);

		IEnumerable<string> completionValues = completionItems
			.Select(static item => item.InsertText)
			.OfType<string>();
		if (dataArgument is not null &&
		    TryGetFileSystemCompletionKind(dataArgument, out var fileSystemKind))
		{
			completionValues = completionValues.Concat(
				FileSystemCompletionSource.Complete(
					ReadCurrentValuePrefix(
						normalized.Value.CommandLine,
						normalized.Value.CursorPosition),
					fileSystemKind));
		}

		var candidates = completionValues
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Where(value => IsVisible(
				value,
				command,
				visibleCommands,
				optionByToken,
				hasCompletedDelimiter,
				acceptsDataCandidates))
			.Where(value => IsAvailable(value, parseResult, optionByToken))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (hasCompletedDelimiter)
		{
			candidates = candidates
				.Where(value => !optionByToken.ContainsKey(value))
				.ToArray();
		}
		if (!hasCompletedDelimiter &&
		    wordToComplete.StartsWith("-", StringComparison.Ordinal))
		{
			candidates = candidates
				.Concat(optionByToken.Keys.Where(optionToken =>
					optionToken.StartsWith(wordToComplete, StringComparison.Ordinal) &&
					IsAvailable(optionToken, parseResult, optionByToken)))
				.Distinct(StringComparer.Ordinal)
				.ToArray();
		}
		if (choiceOption is not null)
		{
			candidates = candidates
				.Where(value => CompletionAvailabilityRegistry.IsValueAvailable(
					choiceOption,
					parseResult,
					value))
				.ToArray();
		}
		if (choiceOption is not null ||
		    choiceArgument is not null)
		{
			var valuePrefix = ResolveChoiceValuePrefix(
				normalized.Value.CommandLine,
				normalized.Value.CursorPosition,
				wordToComplete);
			if (choiceOption?.ValueType == typeof(CliProfileValue))
			{
				candidates = candidates
					.Concat(FileSystemCompletionSource.Complete(
						valuePrefix.Value,
						FileSystemCompletionKind.FilesAndDirectories))
					.Distinct(StringComparer.Ordinal)
					.ToArray();
			}
			candidates = KeepCanonicalChoiceTokens(
				choiceOption?.ValueType ?? choiceArgument!.ValueType,
				candidates);
			candidates = candidates
				.Where(value => value.StartsWith(
					valuePrefix.Value,
					StringComparison.OrdinalIgnoreCase))
				.Select(value => valuePrefix.EqualsPrefix + value)
				.ToArray();
		}

		return candidates
			.OrderBy(static value => value, StringComparer.Ordinal)
			.ToArray();
	}

	private static bool TryGetFileSystemCompletionKind(
		Argument argument,
		out FileSystemCompletionKind kind)
	{
		switch (argument.Name)
		{
			case "PROJECT":
				kind = FileSystemCompletionKind.Directories;
				return true;
			case "FILE":
				kind = FileSystemCompletionKind.FilesAndDirectories;
				return true;
			default:
				kind = default;
				return false;
		}
	}

	private static string ReadCurrentValuePrefix(
		string commandLine,
		int cursorPosition)
	{
		var position = Math.Clamp(cursorPosition, 0, commandLine.Length);
		var tokenStart = 0;
		var activeQuote = '\0';
		for (var index = 0; index < position; index++)
		{
			var character = commandLine[index];
			if (activeQuote == '\0')
			{
				if (char.IsWhiteSpace(character))
					tokenStart = index + 1;
				else if (character is '\'' or '"')
					activeQuote = character;
			}
			else if (character == activeQuote)
			{
				activeQuote = '\0';
			}
		}

		var value = commandLine[tokenStart..position];
		if (value.Length > 0 && value[0] is '\'' or '"')
			value = value[1..];
		if (value.Length > 0 && value[^1] is '\'' or '"')
			value = value[..^1];
		return value;
	}

	private static bool IsVisible(
		string value,
		Command command,
		IReadOnlySet<string> visibleCommands,
		IReadOnlyDictionary<string, Option> optionByToken,
		bool hasCompletedDelimiter,
		bool acceptsDataCandidates)
	{
		if (optionByToken.ContainsKey(value))
			return !hasCompletedDelimiter;
		if (value.StartsWith("-", StringComparison.Ordinal))
			return acceptsDataCandidates;

		var hiddenCommand = command.Subcommands.Any(child =>
			child.Hidden && child.Name.Equals(value, StringComparison.Ordinal));
		if (hiddenCommand)
			return false;
		if (visibleCommands.Contains(value))
			return true;
		return acceptsDataCandidates &&
		       !command.Subcommands.Any(child =>
			       child.Name.Equals(value, StringComparison.Ordinal));
	}

	private static bool IsAvailable(
		string value,
		ParseResult parseResult,
		IReadOnlyDictionary<string, Option> optionByToken)
	{
		if (!optionByToken.TryGetValue(value, out var option))
			return true;
		if (CompletionConflictRegistry.HasExplicitConflict(option, parseResult))
			return false;
		if (!CompletionAvailabilityRegistry.IsOptionAvailable(option, parseResult))
			return false;
		var result = parseResult.GetResult(option);
		if (result is null || result.Implicit)
			return true;
		return option.ValueType.IsArray ||
		       option.Arity.MaximumNumberOfValues > 1;
	}

	private static Option? ResolveValueOptionContext(
		IReadOnlyDictionary<string, Option> optionByToken,
		string commandLine,
		int cursorPosition,
		string wordToComplete)
	{
		var lexeme = ReadLexemeBeforeCursor(commandLine, cursorPosition);
		var equalsIndex = lexeme.IndexOf('=');
		if (equalsIndex >= 0)
		{
			var optionToken = lexeme[..equalsIndex];
			return optionByToken.TryGetValue(optionToken, out var equalsOption) &&
			       RequiresValue(equalsOption)
				? equalsOption
				: null;
		}

		if (wordToComplete.StartsWith("-", StringComparison.Ordinal))
			return null;

		var precedingLexeme = ReadPrecedingLexeme(commandLine, cursorPosition);
		return optionByToken.TryGetValue(precedingLexeme, out var option) &&
		       RequiresValue(option)
			? option
			: null;
	}

	private static bool RequiresValue(Option option) =>
		option.Arity.MaximumNumberOfValues > 0 &&
		option.ValueType != typeof(bool);

	private static Argument? ResolveChoiceArgumentContext(
		ParseResult parseResult,
		Command command,
		string commandLine,
		int cursorPosition,
		string wordToComplete)
	{
		var atNewWord = cursorPosition > 0 &&
		                cursorPosition <= commandLine.Length &&
		                char.IsWhiteSpace(commandLine[cursorPosition - 1]);
		if (!atNewWord &&
		    (wordToComplete.StartsWith("-", StringComparison.Ordinal) ||
		     wordToComplete.Equals(command.Name, StringComparison.Ordinal)))
		{
			return null;
		}
		foreach (var argument in command.Arguments.Where(argument =>
			         IsChoiceValueType(argument.ValueType)))
		{
			var result = parseResult.GetResult(argument);
			if (result is null ||
			    result.Errors.Any() ||
			    !atNewWord)
			{
				return argument;
			}
		}
		return null;
	}

	private static bool HasCompletedDelimiter(
		string commandLine,
		int cursorPosition)
	{
		var position = Math.Clamp(cursorPosition, 0, commandLine.Length);
		var index = 0;
		while (index < position)
		{
			while (index < position && char.IsWhiteSpace(commandLine[index]))
				index++;
			if (index >= position)
				return false;

			if (commandLine[index] is '\'' or '"')
			{
				var quote = commandLine[index++];
				while (index < position && commandLine[index] != quote)
					index++;
				if (index < position)
					index++;
				continue;
			}

			var tokenStart = index;
			while (index < position && !char.IsWhiteSpace(commandLine[index]))
				index++;
			if (commandLine.AsSpan(tokenStart, index - tokenStart)
				    .SequenceEqual("--".AsSpan()) &&
			    index < position)
			{
				return true;
			}
		}
		return false;
	}

	private static Argument? ResolveDataArgumentContext(
		ParseResult parseResult,
		Command command,
		string commandLine,
		int cursorPosition,
		string wordToComplete,
		bool hasCompletedDelimiter)
	{
		var atNewWord = cursorPosition > 0 &&
		                cursorPosition <= commandLine.Length &&
		                char.IsWhiteSpace(commandLine[cursorPosition - 1]);
		if (!atNewWord &&
		    !hasCompletedDelimiter &&
		    (wordToComplete.StartsWith("-", StringComparison.Ordinal) ||
		     wordToComplete.Equals(command.Name, StringComparison.Ordinal)))
		{
			return null;
		}
		foreach (var argument in command.Arguments)
		{
			var result = parseResult.GetResult(argument);
			if (!atNewWord &&
			    result?.Tokens.LastOrDefault()?.Value.Equals(
				    wordToComplete,
				    StringComparison.Ordinal) == true)
			{
				return argument;
			}
			if (result is null ||
			    result.Implicit ||
			    result.Errors.Any() ||
			    result.Tokens.Count < argument.Arity.MaximumNumberOfValues)
			{
				return argument;
			}
		}
		return null;
	}

	private static string ReadLexemeBeforeCursor(
		string commandLine,
		int cursorPosition)
	{
		var end = Math.Clamp(cursorPosition, 0, commandLine.Length);
		var start = end;
		while (start > 0 && !char.IsWhiteSpace(commandLine[start - 1]))
			start--;
		return commandLine[start..end];
	}

	private static string ReadPrecedingLexeme(
		string commandLine,
		int cursorPosition)
	{
		var position = Math.Clamp(cursorPosition, 0, commandLine.Length);
		while (position > 0 && char.IsWhiteSpace(commandLine[position - 1]))
			position--;

		var currentStart = position;
		while (currentStart > 0 && !char.IsWhiteSpace(commandLine[currentStart - 1]))
			currentStart--;
		if (position == cursorPosition)
			position = currentStart;

		while (position > 0 && char.IsWhiteSpace(commandLine[position - 1]))
			position--;
		var precedingStart = position;
		while (precedingStart > 0 &&
		       !char.IsWhiteSpace(commandLine[precedingStart - 1]))
		{
			precedingStart--;
		}
		return commandLine[precedingStart..position];
	}

	private static ChoiceValuePrefix ResolveChoiceValuePrefix(
		string commandLine,
		int cursorPosition,
		string wordToComplete)
	{
		var position = Math.Clamp(cursorPosition, 0, commandLine.Length);
		var start = position;
		while (start > 0 && !char.IsWhiteSpace(commandLine[start - 1]))
			start--;
		var lexeme = commandLine[start..position];
		var equalsIndex = lexeme.IndexOf('=');
		return equalsIndex < 0
			? new ChoiceValuePrefix(wordToComplete, string.Empty)
			: new ChoiceValuePrefix(
				lexeme[(equalsIndex + 1)..],
				lexeme[..(equalsIndex + 1)]);
	}

	private static bool IsChoiceValueType(Type type)
	{
		var valueType = Nullable.GetUnderlyingType(type) ?? type;
		if (valueType.IsArray)
			valueType = valueType.GetElementType() ?? valueType;
		return valueType.IsEnum || valueType == typeof(CliProfileValue);
	}

	private static string[] KeepCanonicalChoiceTokens(
		Type type,
		IEnumerable<string> candidates)
	{
		var values = candidates.ToArray();
		var valueType = Nullable.GetUnderlyingType(type) ?? type;
		if (valueType == typeof(CliProfileValue))
			return values;
		var canonical = values
			.Where(static value => value.Equals(
				value.ToLowerInvariant(),
				StringComparison.Ordinal))
			.ToArray();
		return canonical.Length == 0 ? values : canonical;
	}

	private static NormalizedInvocation? NormalizeInvocation(
		string commandLine,
		int cursorPosition)
	{
		var position = Math.Clamp(cursorPosition, 0, commandLine.Length);
		var start = 0;
		while (start < commandLine.Length && char.IsWhiteSpace(commandLine[start]))
			start++;
		if (start < commandLine.Length && commandLine[start] == '&')
		{
			start++;
			while (start < commandLine.Length && char.IsWhiteSpace(commandLine[start]))
				start++;
		}

		if (start >= commandLine.Length)
			return new NormalizedInvocation(string.Empty, 0);

		var token = ReadInvocationToken(commandLine, ref start);
		if (!IsDevProjexInvocation(token))
			return new NormalizedInvocation(commandLine, position);
		if (position < start)
			return null;

		while (start < commandLine.Length && char.IsWhiteSpace(commandLine[start]))
			start++;
		return new NormalizedInvocation(
			commandLine[start..],
			Math.Max(0, position - start));
	}

	private static string ReadInvocationToken(string commandLine, ref int position)
	{
		var quote = commandLine[position] is '\'' or '"'
			? commandLine[position++]
			: '\0';
		var valueStart = position;
		if (quote != '\0')
		{
			while (position < commandLine.Length && commandLine[position] != quote)
				position++;
			var value = commandLine[valueStart..position];
			if (position < commandLine.Length)
				position++;
			return value;
		}

		while (position < commandLine.Length && !char.IsWhiteSpace(commandLine[position]))
			position++;
		return commandLine[valueStart..position];
	}

	private static bool IsDevProjexInvocation(string value)
	{
		var name = Path.GetFileName(value.Replace('\\', Path.DirectorySeparatorChar));
		return name.Equals("devprojex", StringComparison.OrdinalIgnoreCase) ||
		       name.Equals("devprojex.exe", StringComparison.OrdinalIgnoreCase) ||
		       name.Equals("devprojex.cmd", StringComparison.OrdinalIgnoreCase);
	}

	private readonly record struct NormalizedInvocation(
		string CommandLine,
		int CursorPosition);

	private readonly record struct ChoiceValuePrefix(
		string Value,
		string EqualsPrefix);
}
