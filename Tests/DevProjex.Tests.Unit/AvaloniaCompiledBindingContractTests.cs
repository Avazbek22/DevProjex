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

	[Fact]
	public void SettingsPanel_UsesVirtualizedListsForChecklistSections()
	{
		var viewFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Views",
			"SettingsPanelView.axaml");
		var document = XDocument.Load(viewFile);
		var root = Assert.IsType<XElement>(document.Root);
		var avaloniaNamespace = root.Name.Namespace;

		Assert.Equal(3, root.Descendants(avaloniaNamespace + "ListBox").Count());
		Assert.Equal(3, root.Descendants(avaloniaNamespace + "VirtualizingStackPanel").Count());
		Assert.Empty(root.Descendants(avaloniaNamespace + "ItemsControl"));
		Assert.Empty(root.Descendants(avaloniaNamespace + "ItemsRepeater"));
	}

	[Fact]
	public void SettingsPanel_ApplyAttentionUsesConditionalIdleSafeFadeContract()
	{
		var viewFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Views",
			"SettingsPanelView.axaml");
		var document = XDocument.Load(viewFile);
		var root = Assert.IsType<XElement>(document.Root);
		var avaloniaNamespace = root.Name.Namespace;
		var applyButton = Assert.Single(
			root.Descendants(avaloniaNamespace + "Button"),
			element => element.Attribute("Name")?.Value == "ApplySettingsButton");

		Assert.Equal(
			"{Binding IsApplySettingsAttentionActive}",
			applyButton.Attribute("Classes.apply-attention")?.Value);

		var attentionStyle = Assert.Single(
			root.Descendants(avaloniaNamespace + "Style"),
			element => element.Attribute("Selector")?.Value == "Button#ApplySettingsButton.apply-attention");
		var animation = Assert.Single(attentionStyle.Descendants(avaloniaNamespace + "Animation"));
		Assert.Equal("0:0:1.8", animation.Attribute("Duration")?.Value);
		Assert.Equal("INFINITE", animation.Attribute("IterationCount")?.Value);
		Assert.Equal("OnlyIfVisible", animation.Attribute("PlaybackBehavior")?.Value);
		Assert.Equal("SineEaseInOut", animation.Attribute("Easing")?.Value);

		var opacityValues = animation
			.Descendants(avaloniaNamespace + "Setter")
			.Where(element => element.Attribute("Property")?.Value == "Opacity")
			.Select(element => element.Attribute("Value")?.Value ?? string.Empty)
			.ToArray();
		Assert.Equal(["1", "0.48", "1"], opacityValues);
	}

	[Fact]
	public void ThemePopover_ExposesOnlyMeaningfulEffectControls()
	{
		var viewFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Views",
			"ThemePopoverView.axaml");
		var document = XDocument.Load(viewFile);
		var root = Assert.IsType<XElement>(document.Root);
		var avaloniaNamespace = root.Name.Namespace;
		var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
		var namedEffectControls = root
			.Descendants(avaloniaNamespace + "CheckBox")
			.Select(element => element.Attribute(xamlNamespace + "Name")?.Value)
			.Where(name => name?.EndsWith("EffectCheckBox", StringComparison.Ordinal) == true)
			.OfType<string>()
			.ToArray();
		var sliderBindings = root
			.Descendants(avaloniaNamespace + "Slider")
			.Select(element => element.Attribute("Value")?.Value ?? string.Empty)
			.ToArray();

		Assert.Equal(
			["TransparentEffectCheckBox", "BlurEffectCheckBox", "MicaEffectCheckBox"],
			namedEffectControls);
		Assert.Equal(
			"{Binding IsAcrylicAvailable}",
			root.Descendants(avaloniaNamespace + "CheckBox")
				.Single(element => element.Attribute(xamlNamespace + "Name")?.Value == "BlurEffectCheckBox")
				.Attribute("IsVisible")?.Value);
		Assert.Equal(
			"{Binding IsMicaAvailable}",
			root.Descendants(avaloniaNamespace + "CheckBox")
				.Single(element => element.Attribute(xamlNamespace + "Name")?.Value == "MicaEffectCheckBox")
				.Attribute("IsVisible")?.Value);
		Assert.Contains(sliderBindings, binding => binding.Contains("BackgroundTransparency", StringComparison.Ordinal));
		Assert.Contains(sliderBindings, binding => binding.Contains("MenuTransparency", StringComparison.Ordinal));
		Assert.DoesNotContain(sliderBindings, binding => binding.Contains("MaterialIntensity", StringComparison.Ordinal));
		Assert.Contains(
			root.Descendants(avaloniaNamespace + "Slider"),
			slider => slider.Attribute(xamlNamespace + "Name")?.Value == "MenuTransparencySlider");
		Assert.DoesNotContain(sliderBindings, binding => binding.Contains("BorderStrength", StringComparison.Ordinal));
		Assert.Empty(root.Descendants(avaloniaNamespace + "Expander"));
	}

	[Fact]
	public void ThemeStyles_MainMenuUsesDedicatedPopupBrushAtEveryDepth()
	{
		var styleFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Styles",
			"Theme.axaml");
		var document = XDocument.Load(styleFile);
		var root = Assert.IsType<XElement>(document.Root);
		var avaloniaNamespace = root.Name.Namespace;
		var styles = root.Descendants(avaloniaNamespace + "Style").ToArray();

		AssertStyleBackground(
			styles,
			"Menu.main-menu-strip MenuItem /template/ Popup > Border",
			"MainMenuPopupBrush",
			avaloniaNamespace);
		AssertStyleBackground(styles, "ContextMenu", "MenuPopupBrush", avaloniaNamespace);
		AssertStyleBackground(
			styles,
			"MenuItem MenuItem /template/ Popup > Border",
			"MenuChildPopupBrush",
			avaloniaNamespace);
	}

	[Fact]
	public void ThemeStyles_UnavailableRecentFolderRemainsEnabledButVisuallyMuted()
	{
		var styleFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"Styles",
			"Theme.axaml");
		var document = XDocument.Load(styleFile);
		var root = Assert.IsType<XElement>(document.Root);
		var avaloniaNamespace = root.Name.Namespace;
		var style = Assert.Single(
			root.Descendants(avaloniaNamespace + "Style"),
			element => element.Attribute("Selector")?.Value == "MenuItem.recent-folder-unavailable");

		var opacity = Assert.Single(
			style.Elements(avaloniaNamespace + "Setter"),
			element => element.Attribute("Property")?.Value == "Opacity");
		Assert.Equal("0.5", opacity.Attribute("Value")?.Value);
		Assert.DoesNotContain(
			style.Elements(avaloniaNamespace + "Setter"),
			element => element.Attribute("Property")?.Value == "IsEnabled");
	}

	[Fact]
	public void MainWindow_StartsWithoutSpeculativeBackdropBeforePresetLoading()
	{
		var viewFile = Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"MainWindow.axaml");
		var document = XDocument.Load(viewFile);
		var root = Assert.IsType<XElement>(document.Root);

		Assert.Equal("None", root.Attribute("TransparencyLevelHint")?.Value);
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

	private static void AssertStyleBackground(
		IEnumerable<XElement> styles,
		string selector,
		string brushKey,
		XNamespace avaloniaNamespace)
	{
		var style = Assert.Single(styles, element => element.Attribute("Selector")?.Value == selector);
		var backgroundSetter = Assert.Single(
			style.Elements(avaloniaNamespace + "Setter"),
			element => element.Attribute("Property")?.Value == "Background");

		Assert.Contains(brushKey, backgroundSetter.Attribute("Value")?.Value, StringComparison.Ordinal);
	}
}
