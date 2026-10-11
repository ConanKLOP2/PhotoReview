using System.Windows.Data;
using System.Windows.Markup;

namespace PhotoReview.App.Localization;

/// <summary>
/// <c>{loc:Tr settings.title}</c>: a one-way binding to <see cref="LocalizationSource"/>, so XAML text follows
/// live language switches (ADR 0006). The key must exist in <c>en.json</c> (checked by an architecture test).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("[" + Key + "]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
