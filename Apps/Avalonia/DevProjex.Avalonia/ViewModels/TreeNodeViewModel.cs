using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Avalonia.ViewModels;

public sealed class TreeNodeViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<TreeNodeViewModel> EmptyChildItems = Array.Empty<TreeNodeViewModel>();

    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _isSelected;
    private string _displayName;
    private bool _isCurrentSearchMatch;
    private InlineCollection? _displayInlines;
    private bool _hasHighlightedDisplay;

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
    public IEnumerable<TreeNodeViewModel> ChildItemsSource => Children.Count > 0 ? Children : EmptyChildItems;

    /// <summary>
    /// Indicates whether this node has children. Used to control expander visibility
    /// independently of VirtualizingStackPanel's cached :empty pseudo-class state.
    /// </summary>
    public bool HasChildren => Children.Count > 0;

    public IImage? Icon { get; set; }

    public InlineCollection? DisplayInlines => _displayInlines;

    public bool HasHighlightedDisplay
    {
        get => _hasHighlightedDisplay;
        private set
        {
            if (_hasHighlightedDisplay == value) return;
            _hasHighlightedDisplay = value;
            RaisePropertyChanged();
        }
    }

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
        RaisePropertyChanged(nameof(ChildItemsSource));

        // Clear UI-related objects
        _displayInlines?.Clear();
        _displayInlines = null;
        _hasHighlightedDisplay = false;
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
        if (string.IsNullOrWhiteSpace(query))
        {
            if (_displayInlines is { Count: > 0 })
            {
                _displayInlines.Clear();
                RaisePropertyChanged(nameof(DisplayInlines));
            }

            HasHighlightedDisplay = false;
            return;
        }

        var createdInlines = _displayInlines is null;
        var inlines = _displayInlines ??= new InlineCollection();
        inlines.Clear();

        if (createdInlines)
            RaisePropertyChanged(nameof(DisplayInlines));

        var startIndex = 0;
        while (startIndex < DisplayName.Length)
        {
            var index = DisplayName.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                inlines.Add(new Run(DisplayName[startIndex..]) { Foreground = normalForeground });
                break;
            }

            if (index > startIndex)
                inlines.Add(new Run(DisplayName[startIndex..index]) { Foreground = normalForeground });

            var matchBackground = IsCurrentSearchMatch ? currentHighlightBackground : highlightBackground;
            inlines.Add(new Run(DisplayName.Substring(index, query.Length))
            {
                Background = matchBackground,
                Foreground = highlightForeground
            });

            startIndex = index + query.Length;
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(DisplayName) { Foreground = normalForeground });

        HasHighlightedDisplay = true;
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

        // Single pass through children instead of two LINQ enumerations
        var allChecked = true;
        var anyChecked = false;
        foreach (var child in Children)
        {
            if (child.IsChecked != true)
                allChecked = false;
            if (child.IsChecked != false)
                anyChecked = true;

            // Early exit: if we know result is indeterminate, stop checking
            if (!allChecked && anyChecked)
                break;
        }

        bool? next = allChecked ? true : anyChecked ? null : false;

        if (_isChecked != next)
        {
            _isChecked = next;
            RaisePropertyChanged(nameof(IsChecked));
        }

        Parent?.UpdateCheckedFromChildren();
    }
}
