using System.Collections.Frozen;

namespace DevProjex.Infrastructure.SmartIgnore;

internal static class SmartIgnoreRuleSet
{
	public static readonly IReadOnlySet<string> Empty =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static IReadOnlySet<string> Create(params string[] names) =>
		names.Length == 0
			? Empty
			: names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartIgnoreRuleDescriptor Descriptor(
		IReadOnlySet<string>? markerFiles = null,
		IReadOnlySet<string>? markerExtensions = null,
		IReadOnlySet<string>? folderNames = null,
		IReadOnlySet<string>? fileNames = null) =>
		new(
			markerFiles ?? Empty,
			markerExtensions ?? Empty,
			folderNames ?? Empty,
			fileNames ?? Empty);

	public static SmartIgnoreResult Result(
		IReadOnlySet<string>? folderNames = null,
		IReadOnlySet<string>? fileNames = null) =>
		new(folderNames ?? Empty, fileNames ?? Empty);
}
