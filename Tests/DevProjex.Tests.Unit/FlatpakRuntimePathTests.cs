using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakRuntimePathTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RuntimeDirectoryIsSharedByAppIdOnlyInsideFlatpak(bool windows)
	{
		using var directory = new TemporaryDirectory();
		using (FlatpakEnvironment.UseForTesting(new(true, "test.app")))
			Assert.Equal(Path.Combine(directory.Path, "app", "test.app"), DesktopControlPaths.ResolveDataRoot(
				name => name == "XDG_RUNTIME_DIR" ? directory.Path : null, () => throw new InvalidOperationException(), windows));
		using (FlatpakEnvironment.UseForTesting(new(false, null)))
			Assert.Equal(windows ? "state-root" : directory.Path, DesktopControlPaths.ResolveDataRoot(
				name => name == "XDG_RUNTIME_DIR" ? directory.Path : null, () => "state-root", windows));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("relative")]
	public void MissingOrRelativeRuntimeUsesTheSharedStateRootRatherThanTemp(string? runtime)
	{
		using var directory = new TemporaryDirectory();
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		foreach (var windows in new[] { true, false })
			Assert.Equal(directory.Path, DesktopControlPaths.ResolveDataRoot(name => name == "XDG_RUNTIME_DIR" ? runtime : null, () => directory.Path, windows));
	}

	[Fact]
	public void ExistingInternalRootRetainsPrecedenceInBothModes()
	{
		using var directory = new TemporaryDirectory();
		foreach (var active in new[] { true, false })
		{
			using var environment = FlatpakEnvironment.UseForTesting(new(active, active ? "test.app" : null));
			Assert.Equal(directory.Path, DesktopControlPaths.ResolveDataRoot(name => name == "DEVPROJEX_INTERNAL_DATA_ROOT" ? directory.Path : null,
				() => throw new InvalidOperationException(), false));
		}
	}
}
