using System.Xml.Linq;

namespace DevProjex.Tests.Unit;

public sealed class AvaloniaCompiledBindingContractTests
{
	private static readonly string[] ExplicitCompiledBindingViews =
	[
		"AboutPopoverView.axaml",
		"FilterBarView.axaml",
		"GitCloneWindow.axaml",
		"HelpPopoverView.axaml",
		"SearchBarView.axaml",
		"SettingsPanelView.axaml",
		"ThemePopoverView.axaml"
	];

	[Fact]
	public void AvaloniaProject_UsesCompiledBindingsByDefault()
	{
		var projectFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"DevProjex.Avalonia.csproj");

		var document = XDocument.Load(projectFile);
		var value = document.Descendants("AvaloniaUseCompiledBindingsByDefault").SingleOrDefault()?.Value.Trim();

		Assert.True(bool.TryParse(value, out var enabled));
		Assert.True(enabled);
	}

	[Theory]
	[MemberData(nameof(GetExplicitCompiledBindingViews))]
	public void SmallAvaloniaViews_DeclareTypedCompiledBindingSurface(string fileName)
	{
		var viewFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Views",
			fileName);

		var document = XDocument.Load(viewFile);
		var root = Assert.IsType<XElement>(document.Root);
		var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

		Assert.False(string.IsNullOrWhiteSpace(root.Attribute(xamlNamespace + "DataType")?.Value));
		Assert.Equal("True", root.Attribute(xamlNamespace + "CompileBindings")?.Value);
	}

	public static TheoryData<string> GetExplicitCompiledBindingViews()
	{
		var data = new TheoryData<string>();

		foreach (var file in ExplicitCompiledBindingViews)
		{
			data.Add(file);
		}

		return data;
	}

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;

		while (directory is not null)
		{
			if (Directory.Exists(Path.Combine(directory, ".git")) ||
			    File.Exists(Path.Combine(directory, "DevProjex.sln")))
			{
				return directory;
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
