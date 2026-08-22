namespace DevProjex.Tests.Unit.Avalonia;

public sealed class OptionViewModelTests
{
    [Fact]
    public void SelectionOptionViewModel_Constructor_SetsProperties()
    {
        var option = new SelectionOptionViewModel("Option", true);

        Assert.Equal("Option", option.Name);
        Assert.True(option.IsChecked);
    }

    [Fact]
    public void SelectionOptionViewModel_IsChecked_Changes()
    {
        var option = new SelectionOptionViewModel("Option", false);

        option.IsChecked = true;

        Assert.True(option.IsChecked);
    }

    [Fact]
    public void SelectionOptionViewModel_IsChecked_RaisesCheckedChanged()
    {
        var option = new SelectionOptionViewModel("Option", false);
        var called = false;
        option.CheckedChanged += (_, _) => called = true;

        option.IsChecked = true;

        Assert.True(called);
    }

    [Fact]
    public void SelectionOptionViewModel_IsChecked_SameValueDoesNotRaiseCheckedChanged()
    {
        var option = new SelectionOptionViewModel("Option", false);
        var called = false;
        option.CheckedChanged += (_, _) => called = true;

        option.IsChecked = false;

        Assert.False(called);
    }

    [Fact]
	public void IgnoreOptionViewModel_Constructor_SetsProperties()
	{
		var option = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "Bin", true);

        Assert.Equal(IgnoreOptionId.HiddenFolders, option.Id);
        Assert.Equal("Bin", option.Label);
		Assert.True(option.IsChecked);
	}

	[Fact]
	public void IgnoreOptionViewModel_ControllerGroupEnd_IsExplicitPresentationMetadata()
	{
		var regular = new IgnoreOptionViewModel(IgnoreOptionId.SmartIgnore, "Smart", true);
		var groupEnd = new IgnoreOptionViewModel(
			IgnoreOptionId.TrackedGitFilesOnly,
			"Tracked",
			false,
			isControllerGroupEnd: true);

		Assert.False(regular.IsControllerGroupEnd);
		Assert.True(groupEnd.IsControllerGroupEnd);
	}

    [Fact]
    public void IgnoreOptionViewModel_Label_Changes()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "hidden", false);

        option.Label = "binary";

        Assert.Equal("binary", option.Label);
    }

    [Fact]
    public void IgnoreOptionViewModel_IsChecked_Changes()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot", false);

        option.IsChecked = true;

        Assert.True(option.IsChecked);
    }

    [Fact]
    public void IgnoreOptionViewModel_IsChecked_RaisesCheckedChanged()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.DotFiles, "dot", false);
        var called = false;
        option.CheckedChanged += (_, _) => called = true;

        option.IsChecked = true;

        Assert.True(called);
    }

    [Fact]
    public void IgnoreOptionViewModel_IsChecked_SameValueDoesNotRaiseCheckedChanged()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", false);
        var called = false;
        option.CheckedChanged += (_, _) => called = true;

        option.IsChecked = false;

        Assert.False(called);
    }

    [Fact]
    public void IgnoreOptionViewModel_Id_RemainsStableAfterLabelChange()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden", true);

        option.Label = "hidden-updated";

        Assert.Equal(IgnoreOptionId.HiddenFolders, option.Id);
    }

    [Fact]
    public void SelectionOptionViewModel_CheckedChanged_FiresOncePerChange()
    {
        var option = new SelectionOptionViewModel("Option", false);
        var count = 0;
        option.CheckedChanged += (_, _) => count++;

        option.IsChecked = true;

        Assert.Equal(1, count);
    }

    [Fact]
    public void IgnoreOptionViewModel_CheckedChanged_FiresOncePerChange()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", false);
        var count = 0;
        option.CheckedChanged += (_, _) => count++;

        option.IsChecked = true;

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("Hide secrets (4)", "Hide secrets", "(4)")]
    [InlineData("Hide private data (156)", "Hide private data", "(156)")]
    [InlineData("Hide secrets (4/1)", "Hide secrets", "(4/1)")]
    [InlineData("Скрывать личные данные (12/7)", "Скрывать личные данные", "(12/7)")]
    public void IgnoreOptionViewModel_TrailingCounter_SplitsIntoDisplayNameAndCounterText(
        string label,
        string expectedDisplayName,
        string expectedCounter)
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HideSecrets, label, true);

        Assert.Equal(expectedDisplayName, option.DisplayName);
        Assert.Equal(expectedCounter, option.CounterText);
        Assert.True(option.HasCounter);
    }

    [Theory]
    [InlineData("Hide secrets")]
    [InlineData("Files without extension (no ext)")]
    [InlineData("Strange label ()")]
    [InlineData("Strange label (12/)")]
    [InlineData("Strange label (/12)")]
    [InlineData("Strange label (1/2/3)")]
    [InlineData("(12)")]
    public void IgnoreOptionViewModel_WithoutTrailingCounter_KeepsFullLabelAsDisplayName(string label)
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HideSecrets, label, true);

        Assert.Equal(label, option.DisplayName);
        Assert.Equal(string.Empty, option.CounterText);
        Assert.False(option.HasCounter);
    }

    [Fact]
    public void IgnoreOptionViewModel_LabelChange_RaisesCounterPresentationNotifications()
    {
        var option = new IgnoreOptionViewModel(IgnoreOptionId.HideSecrets, "Hide secrets", true);
        var changed = new List<string?>();
        option.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        option.Label = "Hide secrets (3)";

        Assert.Contains(nameof(IgnoreOptionViewModel.DisplayName), changed);
        Assert.Contains(nameof(IgnoreOptionViewModel.CounterText), changed);
        Assert.Contains(nameof(IgnoreOptionViewModel.HasCounter), changed);
        Assert.Equal("Hide secrets", option.DisplayName);
        Assert.Equal("(3)", option.CounterText);
    }

}

