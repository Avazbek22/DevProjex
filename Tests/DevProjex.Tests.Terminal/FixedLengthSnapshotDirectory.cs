namespace DevProjex.Tests.Terminal;

internal sealed class FixedLengthSnapshotDirectory : IDisposable
{
	private readonly string _ownedRoot;

	public FixedLengthSnapshotDirectory(
		int totalPathLength,
		string? preservedLeafName = null)
	{
		var (path, ownedRoot) = BuildPath(
			ResolveTemporaryRoot(),
			totalPathLength,
			preservedLeafName,
			Guid.NewGuid().ToString("N"));
		Path = path;
		_ownedRoot = ownedRoot;
		DeleteOwnedRoot();
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	internal static string ResolveTemporaryRoot()
	{
		// Use physical Unix roots so macOS aliases cannot change rendered path geometry.
		if (OperatingSystem.IsMacOS())
			return "/private/tmp";
		if (OperatingSystem.IsLinux())
			return "/tmp";
		return System.IO.Path.GetTempPath();
	}

	internal static (string Path, string OwnedRoot) BuildPath(
		string temporaryRoot,
		int totalPathLength,
		string? preservedLeafName,
		string uniqueToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(uniqueToken);
		var root = System.IO.Path
			.GetFullPath(temporaryRoot)
			.TrimEnd(
				System.IO.Path.DirectorySeparatorChar,
				System.IO.Path.AltDirectorySeparatorChar);
		var ownedRootLength = string.IsNullOrEmpty(preservedLeafName)
			? totalPathLength
			: totalPathLength - preservedLeafName.Length - 1;
		var componentLength = ownedRootLength - root.Length - 1;
		if (componentLength < 8)
		{
			throw new ArgumentOutOfRangeException(
				nameof(totalPathLength),
				totalPathLength,
				"The fixed snapshot path leaves no room for an isolated directory name.");
		}

		var component = ResizeComponent(uniqueToken, componentLength);
		var ownedRoot = System.IO.Path.Combine(root, component);
		var path = string.IsNullOrEmpty(preservedLeafName)
			? ownedRoot
			: System.IO.Path.Combine(ownedRoot, preservedLeafName);
		if (path.Length != totalPathLength)
		{
			throw new InvalidOperationException(
				$"Expected a {totalPathLength}-character path but built {path.Length}: {path}");
		}

		return (path, ownedRoot);
	}

	private static string ResizeComponent(string value, int length) =>
		value.Length >= length
			? value[..length]
			: value.PadRight(length, 'x');

	public void Dispose() => DeleteOwnedRoot();

	private void DeleteOwnedRoot()
	{
		if (Directory.Exists(_ownedRoot))
			Directory.Delete(_ownedRoot, recursive: true);
	}
}
