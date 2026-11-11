using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OurTaikoEdit.App.Theme;

public static class UiConverters
{
    // A difficulty's colour from its course as written.
    public static readonly IValueConverter Difficulty = new FuncValueConverter<string?, IBrush>(Ui.Difficulty);
}
