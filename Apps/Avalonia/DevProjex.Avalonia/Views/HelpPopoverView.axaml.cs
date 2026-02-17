using System.ComponentModel;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace DevProjex.Avalonia.Views;

public partial class HelpPopoverView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    private MainWindowViewModel? _boundViewModel;

    public HelpPopoverView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        var bodyPanel = GetBodyPanel();
        if (bodyPanel is null)
            return;

        if (_boundViewModel is not null)
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _boundViewModel = DataContext as MainWindowViewModel;
        if (_boundViewModel is null)
        {
            bodyPanel.Children.Clear();
            return;
        }

        _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BuildBody(bodyPanel, _boundViewModel.HelpHelpBody);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HelpHelpBody) && DataContext is MainWindowViewModel viewModel)
        {
            var bodyPanel = GetBodyPanel();
            if (bodyPanel is null)
                return;

            BuildBody(bodyPanel, viewModel.HelpHelpBody);
        }
    }

    private void BuildBody(StackPanel bodyPanel, string? rawText)
    {
        bodyPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var lines = rawText.Replace("\r\n", "\n").Split('\n');
        bool pendingSpacer = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                pendingSpacer = true;
                continue;
            }

            if (pendingSpacer)
            {
                bodyPanel.Children.Add(new Border { Height = 8 });
                pendingSpacer = false;
            }

            if (TryAddHeading(bodyPanel, trimmed)) continue;
            if (TryAddSubheading(bodyPanel, trimmed)) continue;
            if (TryAddBullet(bodyPanel, trimmed)) continue;
            if (TryAddNumbered(bodyPanel, trimmed)) continue;

            bodyPanel.Children.Add(CreateParagraph(trimmed));
        }
    }

    private bool TryAddHeading(StackPanel bodyPanel, string line)
    {
        if (!line.StartsWith("## ", StringComparison.Ordinal)) return false;
        bodyPanel.Children.Add(CreateHeading(line[3..], 16));
        return true;
    }

    private bool TryAddSubheading(StackPanel bodyPanel, string line)
    {
        if (!line.StartsWith("### ", StringComparison.Ordinal)) return false;
        bodyPanel.Children.Add(CreateHeading(line[4..], 14));
        return true;
    }

    private bool TryAddBullet(StackPanel bodyPanel, string line)
    {
        if (line.Length < 2) return false;
        if (!(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
            return false;

        bodyPanel.Children.Add(CreateBullet(line[2..]));
        return true;
    }

    private bool TryAddNumbered(StackPanel bodyPanel, string line)
    {
        var dotIndex = line.IndexOf(')');
        if (dotIndex <= 0 || dotIndex > 4) return false;
        if (!char.IsDigit(line[0])) return false;
        if (dotIndex + 1 < line.Length && line[dotIndex + 1] == ' ')
        {
            bodyPanel.Children.Add(CreateBullet(line));
            return true;
        }

        return false;
    }

    private Control CreateHeading(string text, double size)
        => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2)
        };

    private Control CreateParagraph(string text)
        => new TextBlock
        {
            Text = text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };

    private Control CreateBullet(string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };

        grid.Children.Add(new TextBlock
        {
            Text = "•",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Top
        });

        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 0, 0, 0)
        });

        Grid.SetColumn(grid.Children[1], 1);
        return grid;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }
    }

    private StackPanel? GetBodyPanel() => this.FindControl<StackPanel>("BodyPanel");

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);
}
