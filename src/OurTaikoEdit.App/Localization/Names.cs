using Avalonia.Data.Converters;
using OurTaikoEdit.Core.Localization;
using OurTaiko;

namespace OurTaikoEdit.App.Localization;

// Names of values shown in lists, in the interface language. The converters take the value and
// the language (bound only so that the name is made again when the language changes).
public static class Names
{
    public static string Route(BranchRoute route) => Loc.T(route switch
    {
        BranchRoute.Expert => "RouteExpert",
        BranchRoute.Master => "RouteMaster",
        _ => "RouteNormal",
    });

    // "ui" is the interface's own; a language is called by its own name.
    public static string MessageLanguage(string? value) => value == "ui" ? Loc.T("SameAsInterface") : Loc.NativeName(value ?? "en");

    public static readonly IMultiValueConverter RouteName
        = new FuncMultiValueConverter<object?, string>(v => v.FirstOrDefault() is BranchRoute r ? Route(r) : "");

    public static readonly IMultiValueConverter MessageLanguageName
        = new FuncMultiValueConverter<object?, string>(v => MessageLanguage(v.FirstOrDefault() as string));
}
