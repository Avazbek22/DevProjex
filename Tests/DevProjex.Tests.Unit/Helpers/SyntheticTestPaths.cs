namespace DevProjex.Tests.Unit.Helpers;

internal static class SyntheticTestPaths
{
	public static string CreateMissingRoot()
	{
		// Synthetic scanner tests must not accidentally target real OS folders such as /root on Linux.
		for (var attempt = 0; attempt < 10; attempt++)
		{
			var path = Path.Combine(
				Path.GetTempPath(),
				"DevProjex",
				"Tests",
				"MissingRoots",
				Guid.NewGuid().ToString("N"));
			if (!Directory.Exists(path) && !File.Exists(path))
				return path;
		}

		throw new InvalidOperationException("Could not allocate a missing synthetic test root path.");
	}
}
