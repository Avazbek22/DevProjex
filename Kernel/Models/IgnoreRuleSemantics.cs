using System.Runtime.CompilerServices;

namespace DevProjex.Kernel.Models;

public static class IgnoreRuleSemantics
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsDotName(string name)
	{
		return !string.IsNullOrEmpty(name) && name[0] == '.';
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool ShouldIgnoreDotDirectory(bool ignoreDotFolders, bool isDot)
	{
		return ignoreDotFolders && isDot;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool ShouldIgnoreHiddenDirectory(
		bool ignoreHiddenFolders,
		bool isHidden,
		bool isDot,
		bool ignoreDotFolders)
	{
		return ShouldIgnoreHiddenEntry(ignoreHiddenFolders, isHidden, isDot, ignoreDotFolders);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool ShouldIgnoreDotFile(bool ignoreDotFiles, bool isDot)
	{
		return ignoreDotFiles && isDot;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsExtensionlessFileName(string fileName)
	{
		if (string.IsNullOrEmpty(fileName))
			return false;

		var dotIndex = fileName.AsSpan().LastIndexOf('.');
		if (dotIndex <= 0)
			return dotIndex != 0;

		return dotIndex == fileName.Length - 1;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool ShouldIgnoreHiddenFile(
		bool ignoreHiddenFiles,
		bool isHidden,
		bool isDot,
		bool ignoreDotFiles)
	{
		return ShouldIgnoreHiddenEntry(ignoreHiddenFiles, isHidden, isDot, ignoreDotFiles);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ShouldIgnoreHiddenEntry(
		bool ignoreHidden,
		bool isHidden,
		bool isDot,
		bool ignoreDotEntry)
	{
		if (!ignoreHidden || !isHidden)
			return false;

		if (!isDot)
			return true;

		if (ignoreDotEntry)
			return false;

		// Unix-like filesystems report dot-named entries as hidden by convention.
		// The dot toggle owns that overlap; Windows can still expose a real Hidden attribute.
		return OperatingSystem.IsWindows();
	}
}
