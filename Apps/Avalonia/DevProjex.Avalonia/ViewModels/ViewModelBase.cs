using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DevProjex.Avalonia.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    // Cache PropertyChangedEventArgs to avoid allocating new instances on every notification.
    // Thread-safe and shared across all ViewModels.
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> EventArgsCache = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null) return;

        var args = EventArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, args);
    }
}
