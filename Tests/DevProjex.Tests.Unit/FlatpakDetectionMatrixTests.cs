namespace DevProjex.Tests.Unit;

public sealed class FlatpakDetectionMatrixTests
{
	public static IEnumerable<object?[]> Cases()
	{
		foreach (var linux in new[] { false, true })
			foreach (var marker in new[] { false, true })
				foreach (var id in new[] { null, "", " ", "\t\r\n", "io.github.Avazbek22.DevProjex" })
					yield return [linux, marker, id];
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public void AllMarkerCombinationsHaveOneResult(bool linux, bool marker, string? id)
	{
		var filesystemQueries = 0;
		var result = FlatpakEnvironment.Detect(linux, _ => id, path =>
		{
			Assert.Equal("/.flatpak-info", path);
			filesystemQueries++;
			return marker;
		});
		Assert.Equal(linux && marker && !string.IsNullOrWhiteSpace(id), result.IsActive);
		Assert.Equal(linux && !string.IsNullOrWhiteSpace(id) ? 1 : 0, filesystemQueries);
	}
}
