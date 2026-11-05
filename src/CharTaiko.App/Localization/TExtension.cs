using Avalonia.Data;
using CharTaiko.Core.Localization;

namespace CharTaiko.App.Localization;

// {l:T Key}: the word for a key in the interface language, following a change of language.
public sealed class TExtension(string key)
{
    public string Key { get; } = key;

    public object ProvideValue(IServiceProvider services)
        => new ReflectionBinding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
