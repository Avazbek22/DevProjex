using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class ExtensionOptionProjectionTests
{
	[Fact]
	public void SplitAvailableEntries_SeparatesExtensionlessNamesWithoutChangingVisibleOrder()
	{
		var source = new[] { ".cs", "Dockerfile", ".json", "Makefile", "LICENSE.", ".tar.gz" };
		var visible = new List<string>();

		var extensionlessCount = ExtensionOptionProjection.SplitAvailableEntries(source, visible);

		Assert.Equal(3, extensionlessCount);
		Assert.Equal([".cs", ".json", ".tar.gz"], visible);
	}

	[Theory]
	[InlineData("", false)]
	[InlineData(" ", false)]
	[InlineData(".gitignore", false)]
	[InlineData("Dockerfile", true)]
	[InlineData("LICENSE.", true)]
	[InlineData("Program.cs", false)]
	[InlineData("archive.tar.gz", false)]
	public void IsExtensionlessEntry_UsesTheSameContractForAllSelectionConsumers(
		string value,
		bool expected)
	{
		Assert.Equal(expected, ExtensionOptionProjection.IsExtensionlessEntry(value));
	}

	[Fact]
	public void BuildResolvedPolicy_IncludesOnlyCheckedOptionsCaseInsensitively()
	{
		var options = new[]
		{
			new SelectionOption(".cs", IsChecked: true),
			new SelectionOption(".JSON", IsChecked: false),
			new SelectionOption(".md", IsChecked: true)
		};

		var policy = ExtensionOptionProjection.BuildResolvedPolicy(options);

		Assert.True(policy.AllowsExtension(".CS"));
		Assert.True(policy.AllowsExtension(".md"));
		Assert.False(policy.AllowsExtension(".json"));
	}
}
