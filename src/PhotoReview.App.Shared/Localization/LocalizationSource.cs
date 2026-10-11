using System.ComponentModel;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Localization;

/// <summary>
/// Binding source for <see cref="TrExtension"/>: <c>this[key]</c> is the current translation, and a
/// language switch raises one <c>Item[]</c> change so every bound text refreshes without a restart.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    public static LocalizationSource Instance { get; } = new();

    private LocalizationSource()
    {
        Localizer.CurrentChanged += (_, _) => PropertyChanged?.Invoke(this, IndexerChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Localizer.Current.Get(key);
}
