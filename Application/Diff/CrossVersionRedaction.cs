using DevProjex.Application.Secrets;

namespace DevProjex.Application.Diff;

/// <summary>
/// Masks a value in both versions when either version had it masked. Detection is per text: a
/// hand mark is anchored to an offset in the working file, and a rule may need context the other
/// version lacks, so the same value can be found in one version and missed in the other. A diff
/// shows both versions side by side, which would put the missed copy next to its placeholder.
/// </summary>
internal static class CrossVersionRedaction
{
	// Shorter values would turn ordinary words into placeholders; detected secrets are far longer.
	// The limit applies to a whole value only, never to a token found inside a longer one.
	internal const int MinimumPropagatedLength = 4;

	public static (string Old, string New) Apply(
		string oldSource,
		SecretTextRedactionResult oldResult,
		string newSource,
		SecretTextRedactionResult newResult,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
		Collect(oldSource, oldResult, placeholders, cancellationToken);
		Collect(newSource, newResult, placeholders, cancellationToken);
		if (placeholders.Count == 0)
			return (oldResult.Text, newResult.Text);

		var automaton = AhoCorasickAutomaton.Build(placeholders.Keys, cancellationToken);
		return (
			Mask(oldSource, oldResult, automaton, placeholders, cancellationToken),
			Mask(newSource, newResult, automaton, placeholders, cancellationToken));
	}

	private static void Collect(
		string source,
		SecretTextRedactionResult result,
		Dictionary<string, string> placeholders,
		CancellationToken cancellationToken)
	{
		var map = result.CoordinateMap;
		for (var index = 0; index < map.RangeCount; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var (sourceStart, sourceLength, transformedStart, transformedLength) = map.GetRange(index);
			if (sourceLength < MinimumPropagatedLength)
				continue;
			placeholders.TryAdd(
				source.Substring(sourceStart, sourceLength),
				result.Text.Substring(transformedStart, transformedLength));
		}
	}

	/// <summary>
	/// Rebuilds the masked text from the raw source, extending every range this text's own
	/// redaction already masked there to cover the full union with any known value found by the
	/// global scan, so the result never shows a partially masked value. The scan runs over the
	/// untouched source, since a value already masked by this text's own redaction is no longer
	/// present, verbatim, in <see cref="SecretTextRedactionResult.Text"/> for that occurrence.
	/// </summary>
	private static string Mask(
		string source,
		SecretTextRedactionResult result,
		AhoCorasickAutomaton automaton,
		IReadOnlyDictionary<string, string> placeholders,
		CancellationToken cancellationToken)
	{
		var occurrences = automaton.FindAllOccurrences(source, cancellationToken);
		if (occurrences.Count == 0)
			return result.Text;

		var map = result.CoordinateMap;
		var spans = new List<ValueSpan>(map.RangeCount + occurrences.Count);
		for (var index = 0; index < map.RangeCount; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var (sourceStart, sourceLength, transformedStart, transformedLength) = map.GetRange(index);
			spans.Add(new ValueSpan(
				sourceStart,
				sourceStart + sourceLength,
				result.Text.Substring(transformedStart, transformedLength),
				IsCandidate: false));
		}
		foreach (var (start, length, value) in occurrences)
			spans.Add(new ValueSpan(start, start + length, placeholders[value], IsCandidate: true));

		var merged = MergeSpans(spans, cancellationToken);
		var builder = new StringBuilder(result.Text.Length);
		var cursor = 0;
		foreach (var span in merged)
		{
			builder.Append(source, cursor, span.Start - cursor).Append(span.Placeholder);
			cursor = span.End;
		}
		builder.Append(source, cursor, source.Length - cursor);
		return builder.ToString();
	}

	/// <summary>
	/// Merges the ranges this text's own redaction already masked with every value occurrence
	/// found by the global scan, in source-text coordinates, into one pass of non-overlapping
	/// spans. Spans are sorted by start, then by the longest end first, so the longest match at a
	/// shared start position wins; where a merged group touches a globally found value, that
	/// value's canonical placeholder covers the whole union, overriding the narrower one this
	/// text's own redaction applied on its own.
	/// </summary>
	private static List<ValueSpan> MergeSpans(List<ValueSpan> spans, CancellationToken cancellationToken)
	{
		spans.Sort(static (left, right) =>
			left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));

		var merged = new List<ValueSpan>(spans.Count);
		ValueSpan? current = null;
		for (var index = 0; index < spans.Count; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var span = spans[index];
			if (current is null)
			{
				current = span;
				continue;
			}
			var accumulated = current.Value;
			if (span.Start < accumulated.End)
			{
				var preferSpan = span.IsCandidate && !accumulated.IsCandidate;
				current = accumulated with
				{
					End = Math.Max(accumulated.End, span.End),
					Placeholder = preferSpan ? span.Placeholder : accumulated.Placeholder,
					IsCandidate = accumulated.IsCandidate || span.IsCandidate
				};
			}
			else
			{
				merged.Add(accumulated);
				current = span;
			}
		}
		if (current is { } last)
			merged.Add(last);
		return merged;
	}

	private readonly record struct ValueSpan(int Start, int End, string Placeholder, bool IsCandidate);

	/// <summary>
	/// Finds every occurrence of any of a fixed set of values in one pass over the text, in time
	/// proportional to the text length plus the total pattern length, rather than one scan per
	/// value.
	/// </summary>
	private sealed class AhoCorasickAutomaton
	{
		private readonly List<Dictionary<char, int>> transitions = [[]];
		private readonly List<int> failureLinks = [0];
		private readonly List<List<int>> endingPatterns = [[]];
		private readonly string[] patterns;

		private AhoCorasickAutomaton(string[] patterns)
		{
			this.patterns = patterns;
		}

		public static AhoCorasickAutomaton Build(IEnumerable<string> values, CancellationToken cancellationToken)
		{
			var patterns = values.ToArray();
			var automaton = new AhoCorasickAutomaton(patterns);
			for (var index = 0; index < patterns.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				automaton.Insert(patterns[index], index);
			}
			automaton.BuildFailureLinks(cancellationToken);
			return automaton;
		}

		private void Insert(string pattern, int patternIndex)
		{
			var node = 0;
			foreach (var character in pattern)
			{
				if (!transitions[node].TryGetValue(character, out var next))
				{
					next = transitions.Count;
					transitions.Add([]);
					failureLinks.Add(0);
					endingPatterns.Add([]);
					transitions[node][character] = next;
				}
				node = next;
			}
			endingPatterns[node].Add(patternIndex);
		}

		private void BuildFailureLinks(CancellationToken cancellationToken)
		{
			var queue = new Queue<int>();
			foreach (var child in transitions[0].Values)
			{
				failureLinks[child] = 0;
				queue.Enqueue(child);
			}
			while (queue.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var current = queue.Dequeue();
				foreach (var (character, child) in transitions[current])
				{
					queue.Enqueue(child);
					var fallback = failureLinks[current];
					while (fallback != 0 && !transitions[fallback].ContainsKey(character))
						fallback = failureLinks[fallback];
					failureLinks[child] = transitions[fallback].TryGetValue(character, out var linked) && linked != child
						? linked
						: 0;
				}
			}
		}

		/// <summary>Every (start, length, value) occurrence of any pattern, in no particular order.</summary>
		public List<(int Start, int Length, string Value)> FindAllOccurrences(string text, CancellationToken cancellationToken)
		{
			var results = new List<(int Start, int Length, string Value)>();
			var node = 0;
			for (var position = 0; position < text.Length; position++)
			{
				if ((position & 0xFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var character = text[position];
				while (node != 0 && !transitions[node].ContainsKey(character))
					node = failureLinks[node];
				node = transitions[node].TryGetValue(character, out var next) ? next : 0;

				var reporter = node;
				while (reporter != 0)
				{
					foreach (var patternIndex in endingPatterns[reporter])
					{
						var value = patterns[patternIndex];
						results.Add((position - value.Length + 1, value.Length, value));
					}
					reporter = failureLinks[reporter];
				}
			}
			return results;
		}
	}
}
