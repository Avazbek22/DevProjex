using System.Collections.Generic;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Kernel.Models;
using Xunit;

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

}

