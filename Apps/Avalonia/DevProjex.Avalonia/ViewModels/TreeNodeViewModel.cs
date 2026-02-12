using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Avalonia.ViewModels;

public sealed class TreeNodeViewModel : ViewModelBase
{
    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _isSelected;
    private string _displayName;
    private bool _isCurrentSearchMatch;

    /// <summary>
    /// Raised when checkbox state changes. Used for real-time metrics updates.
    /// Only fires on user-initiated changes (not cascading updates from parent/children).
    /// </summary>
    public static event EventHandler? GlobalCheckedChanged;

    public TreeNodeViewModel(
        TreeNodeDescriptor descriptor,
        TreeNodeViewModel? parent,
        IImage? icon)
    {
        Descriptor = descriptor;
        Parent = parent;
        _displayName = descriptor.DisplayName;
        Icon = icon;
        // Pre-allocate capacity based on descriptor children count
        Children = new List<TreeNodeViewModel>(descriptor.Children.Count);
    }

    public TreeNodeDescriptor Descriptor { get; private set; }

    public TreeNodeViewModel? Parent { get; private set; }

    public IList<TreeNodeViewModel> Children { get; }

    public IImage? Icon { get; set; }

    public InlineCollection DisplayInlines { get; } = new();

    public bool IsCurrentSearchMatch
    {
        get => _isCurrentSearchMatch;
        set
        {
            if (_isCurrentSearchMatch == value) return;
            _isCurrentSearchMatch = value;
            RaisePropertyChanged();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            RaisePropertyChanged();
        }
    }

    public string FullPath => Descriptor.FullPath;

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            if (value is null)
            {
                SetChecked(false, updateChildren: true, updateParent: true);
                return;
            }
            SetChecked(value, updateChildren: true, updateParent: true);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            RaisePropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            RaisePropertyChanged();
        }
    }

    public void SetExpandedRecursive(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var child in Children)
            child.SetExpandedRecursive(expanded);
    }

    public IEnumerable<TreeNodeViewModel> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
                yield return descendant;
        }
    }

    public void EnsureParentsExpanded()
    {
        var current = Parent;
        while (current is not null)
        {
            current.IsExpanded = true;
            current = current.Parent;
        }
    }

    public void UpdateIcon(IImage? icon)
    {
        Icon = icon;
        RaisePropertyChanged(nameof(Icon));
    }

    /// <summary>
    /// Recursively clears all children and releases references to help GC.
    /// Call before removing from parent collection.
    /// </summary>
    public void ClearRecursive()
    {
        // Clear children recursively first
        foreach (var child in Children)
            child.ClearRecursive();

        // Clear the children list
        Children.Clear();
        if (Children is List<TreeNodeViewModel> list)
            list.TrimExcess();

        // Clear UI-related objects
        DisplayInlines.Clear();
        Icon = null;

        // Break circular references to help GC
        Parent = null;
        Descriptor = null!;
    }

    public void UpdateSearchHighlight(
        string? query,
        IBrush? highlightBackground,
        IBrush? highlightForeground,
        IBrush? normalForeground,
        IBrush? currentHighlightBackground)
    {
        DisplayInlines.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            DisplayInlines.Add(new Run(DisplayName) { Foreground = normalForeground });
            RaisePropertyChanged(nameof(DisplayInlines));
            return;
        }

        var startIndex = 0;
        while (startIndex < DisplayName.Length)
        {
            var index = DisplayName.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                DisplayInlines.Add(new Run(DisplayName[startIndex..]) { Foreground = normalForeground });
                break;
            }

            if (index > startIndex)
                DisplayInlines.Add(new Run(DisplayName[startIndex..index]) { Foreground = normalForeground });

            var matchBackground = IsCurrentSearchMatch ? currentHighlightBackground : highlightBackground;
            DisplayInlines.Add(new Run(DisplayName.Substring(index, query.Length))
            {
                Background = matchBackground,
                Foreground = highlightForeground
            });

            startIndex = index + query.Length;
        }

        if (DisplayInlines.Count == 0)
            DisplayInlines.Add(new Run(DisplayName) { Foreground = normalForeground });

        RaisePropertyChanged(nameof(DisplayInlines));
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        _isChecked = value;
        RaisePropertyChanged(nameof(IsChecked));

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
                child.SetChecked(value.Value, updateChildren: true, updateParent: false);
        }

        if (updateParent)
        {
            Parent?.UpdateCheckedFromChildren();
            // Fire global event for metrics recalculation (only on user-initiated top-level change)
            GlobalCheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateCheckedFromChildren()
    {
        if (Children.Count == 0)
            return;

        bool allChecked = Children.All(child => child.IsChecked == true);
        bool anyChecked = Children.Any(child => child.IsChecked != false);
        bool? next = allChecked ? true : anyChecked ? null : false;

        if (_isChecked != next)
        {
            _isChecked = next;
            RaisePropertyChanged(nameof(IsChecked));
        }

        Parent?.UpdateCheckedFromChildren();
    }
}
