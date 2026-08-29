using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

/// <summary>
/// Keeps extensionless classification and resolved extension policies identical
/// across refresh, live-count, and UI compatibility paths.
/// </summary>
public static class ExtensionOptionProjection
{
	public static void AddCanonicalExtension(
		HashSet<string> extensions,
		ReadOnlySpan<char> extension)
	{
		ArgumentNullException.ThrowIfNull(extensions);
		if (extension.IsEmpty)
			return;

		Span<char> normalized = extension.Length <= 128
			? stackalloc char[extension.Length]
			: new char[extension.Length];
		for (var index = 0; index < extension.Length; index++)
			normalized[index] = char.ToLowerInvariant(extension[index]);

		if (extensions.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) && lookup.Contains(normalized))
			return;

		extensions.Add(normalized.ToString());
	}

	public static int SplitAvailableEntries(
		IReadOnlyCollection<string> source,
		ICollection<string> visibleExtensions)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(visibleExtensions);

		var extensionlessEntriesCount = 0;
		foreach (var entry in source)
		{
			if (IsExtensionlessEntry(entry))
			{
				extensionlessEntriesCount++;
				continue;
			}

			visibleExtensions.Add(entry);
		}

		return extensionlessEntriesCount;
	}

	public static IExtensionInclusionPolicy BuildResolvedPolicy(
		IReadOnlyList<SelectionOption> extensionOptions)
	{
		ArgumentNullException.ThrowIfNull(extensionOptions);

		var selected = new HashSet<string>(extensionOptions.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var option in extensionOptions)
		{
			if (option.IsChecked)
				selected.Add(option.Name);
		}

		return new ExtensionSetInclusionPolicy(selected);
	}

	public static IReadOnlyList<SelectionOption> ApplyExactSelection(
		IReadOnlyList<SelectionOption> extensionOptions,
		IReadOnlySet<string> selectedExtensions)
	{
		ArgumentNullException.ThrowIfNull(extensionOptions);
		ArgumentNullException.ThrowIfNull(selectedExtensions);

		return extensionOptions
			.Select(option => option with { IsChecked = selectedExtensions.Contains(option.Name) })
			.ToArray();
	}

	public static bool IsExtensionlessEntry(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var extension = Path.GetExtension(value.AsSpan());
		return extension.IsEmpty || extension.SequenceEqual(".");
	}
}
