using Avalonia.Controls.Documents;
using System.Runtime.CompilerServices;

namespace DevProjex.Avalonia.ViewModels;

public sealed class TreeNodeViewModel(
    TreeNodeDescriptor descriptor,
    TreeNodeViewModel? parent,
    IImage? icon,
    Func<TreeNodeViewModel, IReadOnlyList<TreeNodeViewModel>>? childrenFactory = null,
    Action<TreeNodeViewModel>? checkedChanged = null)
    : ViewModelBase
{
    private const double DefaultTreeIndentSize = 16;
    private static readonly IReadOnlyList<TreeNodeViewModel> EmptyChildItems = [];
    private static int _preserveDescendantExpansionStateDepth;

    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _isSelected;
    private string _displayName = descriptor.DisplayName;
    private bool _isCurrentSearchMatch;
    private InlineCollection? _displayInlines;
    private bool _hasHighlightedDisplay;
    private int _searchSelfMatchEpoch;
    private int _searchDescendantMatchEpoch;
    private bool _deferredChildCheckedState;
    private List<TreeNodeViewModel> _children = new(descriptor.Children.Count);
    private Func<TreeNodeViewModel, IReadOnlyList<TreeNodeViewModel>>? _childrenFactory = childrenFactory;
    private bool _childrenInitialized = childrenFactory is null || descriptor.Children.Count == 0;
    private readonly Action<TreeNodeViewModel>? _checkedChanged = checkedChanged ?? parent?._checkedChanged;

    // Pre-allocate capacity based on descriptor children count

    public TreeNodeDescriptor Descriptor { get; private set; } = descriptor;

    public TreeNodeViewModel? Parent { get; private set; } = parent;
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;
    public GridLength IndentWidth { get; } =
        new(Math.Max(0, parent is null ? 0 : parent.Depth + 1) * DefaultTreeIndentSize);

    public IList<TreeNodeViewModel> Children => EnsureChildrenRealized();
    public IEnumerable<TreeNodeViewModel> ChildItemsSource => HasChildren ? EnsureChildrenRealized() : EmptyChildItems;

    /// <summary>
    /// Indicates whether this node has children. Used to control expander visibility
    /// independently of VirtualizingStackPanel's cached :empty pseudo-class state.
    /// </summary>
    public bool HasChildren => _children.Count > 0 || (!_childrenInitialized && Descriptor is not null && Descriptor.Children.Count > 0);

    public IImage? Icon { get; set; } = icon;

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

            if (!value && _children.Count > 0 && Volatile.Read(ref _preserveDescendantExpansionStateDepth) == 0)
            {
                // Manual collapse should reset descendant expansion state so reopening the branch
                // behaves predictably and does not immediately realize the entire previously-open subtree.
                using var _ = BeginPreserveDescendantExpansionStateScope();
                foreach (var child in _children)
                    child.SetExpandedRecursive(false);
            }

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
        if (!expanded)
        {
            using var _ = BeginPreserveDescendantExpansionStateScope();
            SetExpandedRecursiveCore(this, expanded);
            return;
        }

        SetExpandedRecursiveCore(this, expanded);
    }

    /// <summary>
    /// Enumerates this node and all descendants using a stack-based approach.
    /// Avoids recursive yield return which creates O(N) state machine objects.
    /// </summary>
    public IEnumerable<TreeNodeViewModel> Flatten()
    {
        var stack = new Stack<TreeNodeViewModel>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            var children = current.EnsureChildrenRealized();
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    /// <summary>
    /// Traverses all descendants of the given roots without allocating an IEnumerable.
    /// Use this in hot paths where Flatten() + SelectMany overhead is undesirable.
    /// </summary>
    public static void ForEachDescendant(IList<TreeNodeViewModel> roots, Action<TreeNodeViewModel> action)
    {
        var stack = new Stack<TreeNodeViewModel>();
        for (var i = roots.Count - 1; i >= 0; i--)
            stack.Push(roots[i]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            action(current);
            var children = current.EnsureChildrenRealized();
            for (var j = children.Count - 1; j >= 0; j--)
                stack.Push(children[j]);
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

    /// <summary>
    /// Collects the minimal checked-path snapshot without forcing lazy subtree realization.
    /// A checked directory represents the whole subtree, so descendants do not need to be
    /// materialized or added individually.
    /// </summary>
    public void CollectCheckedPaths(HashSet<string> selected)
    {
        if (_isChecked == true)
        {
            selected.Add(FullPath);
            return;
        }

        if (!_childrenInitialized)
            return;

        foreach (var child in _children)
            child.CollectCheckedPaths(selected);
    }

    public void UpdateIcon(IImage? icon)
    {
        Icon = icon;
        RaisePropertyChanged(nameof(Icon));
    }

    public static IDisposable BeginPreserveDescendantExpansionStateScope()
    {
        Interlocked.Increment(ref _preserveDescendantExpansionStateDepth);
        return new DescendantExpansionStateScope();
    }

    /// <summary>
    /// Recursively clears all children and releases references to help GC.
    /// Call before removing from parent collection.
    /// </summary>
    public void ClearRecursive()
    {
        // Clear children recursively first
        foreach (var child in _children)
            child.ClearRecursive();

        // Clear the children list
        _children.Clear();
        _children.TrimExcess();
        _childrenInitialized = true;
        _childrenFactory = null;
        RaisePropertyChanged(nameof(ChildItemsSource));
        RaisePropertyChanged(nameof(HasChildren));

        // Clear UI-related objects
        _displayInlines?.Clear();
        _displayInlines = null;
        _hasHighlightedDisplay = false;
        _searchSelfMatchEpoch = 0;
        _searchDescendantMatchEpoch = 0;
        Icon = null;

        // Break circular references to help GC
        Parent = null;
        Descriptor = null!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSearchSelfMatch(int epoch) => _searchSelfMatchEpoch = epoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSearchDescendantMatch(int epoch) => _searchDescendantMatchEpoch = epoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSearchSelfMatch(int epoch) => _searchSelfMatchEpoch == epoch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSearchDescendantMatch(int epoch) => _searchDescendantMatchEpoch == epoch;

    public void UpdateSearchHighlight(
        string? query,
        IBrush? highlightBackground,
        IBrush? highlightForeground,
        IBrush? normalForeground,
        IBrush? currentHighlightBackground)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearSearchHighlight();
            return;
        }

        var firstMatchIndex = DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (firstMatchIndex < 0)
        {
            ClearSearchHighlight();
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
            var index = startIndex == 0
                ? firstMatchIndex
                : DisplayName.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
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

    private void ClearSearchHighlight()
    {
        if (_displayInlines is not null)
        {
            _displayInlines.Clear();
            _displayInlines = null;
            RaisePropertyChanged(nameof(DisplayInlines));
        }

        HasHighlightedDisplay = false;
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value)
        {
            if (value.HasValue)
                _deferredChildCheckedState = value.Value;
            return;
        }

        _isChecked = value;
        RaisePropertyChanged(nameof(IsChecked));

        if (value.HasValue)
            _deferredChildCheckedState = value.Value;

        if (updateChildren && value.HasValue)
        {
            // Only propagate to already realized children. Unrealized branches inherit the
            // latest explicit state when they are materialized later, which keeps checkbox
            // toggles responsive even on very large trees.
            foreach (var child in _children)
                child.SetChecked(value.Value, updateChildren: true, updateParent: false);
        }

        if (updateParent)
        {
            Parent?.UpdateCheckedFromChildren();
            _checkedChanged?.Invoke(this);
        }
    }

    private void UpdateCheckedFromChildren()
    {
        if (_children.Count == 0)
            return;

        // Single pass through children instead of two LINQ enumerations
        var allChecked = true;
        var anyChecked = false;
        foreach (var child in _children)
        {
            if (child.IsChecked != true)
                allChecked = false;
            if (child.IsChecked != false)
                anyChecked = true;

            // Early exit: if we know result is indeterminate, stop checking
            if (!allChecked && anyChecked)
                break;
        }

        if (_children.Count < (Descriptor?.Children.Count ?? _children.Count))
            allChecked = false;

        bool? next = allChecked ? true : anyChecked ? null : false;

        if (_isChecked != next)
        {
            _isChecked = next;
            RaisePropertyChanged(nameof(IsChecked));
        }

        Parent?.UpdateCheckedFromChildren();
    }

    private static void SetExpandedRecursiveCore(TreeNodeViewModel node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.EnsureChildrenRealized())
            SetExpandedRecursiveCore(child, expanded);
    }

    private List<TreeNodeViewModel> EnsureChildrenRealized()
    {
        if (_childrenInitialized)
            return _children;

        // Deeper branches are materialized on demand so initial project load does not pay
        // for the entire view-model graph before the user expands or traverses that subtree.
        var builtChildren = _childrenFactory?.Invoke(this) ?? [];
        _children = builtChildren as List<TreeNodeViewModel> ?? new List<TreeNodeViewModel>(builtChildren);
        _childrenFactory = null;
        _childrenInitialized = true;

        // Newly materialized children must inherit the last explicit subtree state without
        // forcing recursive realization of their own descendants.
        foreach (var child in _children)
            child.SetChecked(_deferredChildCheckedState, updateChildren: false, updateParent: false);

        RaisePropertyChanged(nameof(ChildItemsSource));
        return _children;
    }

    private readonly struct DescendantExpansionStateScope : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref _preserveDescendantExpansionStateDepth);
        }
    }
}
