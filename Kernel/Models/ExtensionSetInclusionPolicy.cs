namespace DevProjex.Kernel.Models;

public sealed class ExtensionSetInclusionPolicy(IReadOnlySet<string> allowedExtensions)
	: IExtensionInclusionPolicy
{
	private readonly HashSet<string>? _hashSet = allowedExtensions as HashSet<string>;

	public bool AllowsExtension(string extension) =>
		!string.IsNullOrWhiteSpace(extension) &&
		AllowsExtension(extension.AsSpan());

	public bool AllowsExtension(ReadOnlySpan<char> extension)
	{
		if (extension.IsWhiteSpace())
			return false;

		if (_hashSet is not null &&
		    _hashSet.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
		{
			return lookup.Contains(extension);
		}

		return allowedExtensions.Contains(extension.ToString());
	}
}
