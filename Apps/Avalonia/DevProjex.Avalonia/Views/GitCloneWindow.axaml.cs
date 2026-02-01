using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DevProjex.Avalonia.Views;

public partial class GitCloneWindow : Window
{
    public event EventHandler<RoutedEventArgs>? StartCloneRequested;
    public event EventHandler<RoutedEventArgs>? CancelRequested;
    private readonly TextBox? _urlTextBox;

    public GitCloneWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _urlTextBox = this.FindControl<TextBox>("UrlTextBox");

        // Focus URL textbox when window opens
        Opened += (_, _) =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _urlTextBox?.Focus();
                _urlTextBox?.SelectAll();
            }, global::Avalonia.Threading.DispatcherPriority.Input);
        };
    }

    public TextBox? UrlTextBoxControl => _urlTextBox;

    private void OnStartClone(object? sender, RoutedEventArgs e)
    {
        StartCloneRequested?.Invoke(this, e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, e);
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartCloneRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
