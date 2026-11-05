using System.ComponentModel;
using System.Globalization;

namespace CharTaiko.Core.Localization;

// The interface's words in English, Chinese (simplified) or Japanese. Views bind to the indexer
// (and follow a change of language at once); code reads T, F and Plural when it makes a message.
// A key missing from a language falls back to English, then to the key itself.
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<string> Languages { get; } = new[] { "en", "zh", "ja" };

    string language = "en";

    // "en", "zh" or "ja".
    public string Language
    {
        get => language;
        set
        {
            string next = Languages.Contains(value) ? value : "en";
            if (next == language) return;
            language = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            Changed?.Invoke();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    // After the language changed: for words made in code, which must be made again.
    public event Action? Changed;

    public string this[string key] => Get(key);

    string Get(string key)
    {
        if (!Strings.Table.TryGetValue(key, out var words)) return key;
        string? word = language switch { "zh" => words.Zh, "ja" => words.Ja, _ => words.En };
        return string.IsNullOrEmpty(word) ? words.En : word;
    }

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    // English needs a singular: a key ending in "One" is its word for one (Chinese and Japanese have none).
    public static string Plural(string key, int count)
        => F(count == 1 && Strings.Table.TryGetValue(key + "One", out var one) && Instance.language == "en" && one.En.Length > 0 ? key + "One" : key, count);

    // The language a setting names: "system" (or anything unknown) picks by the system's culture.
    public static string Resolve(string? setting, CultureInfo? culture = null)
    {
        if (setting != null && Languages.Contains(setting)) return setting;
        string name = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return Languages.Contains(name) ? name : "en";
    }

    // A language's own name, the same in every interface language.
    public static string NativeName(string language) => language switch
    {
        "zh" => "简体中文",
        "ja" => "日本語",
        _ => "English",
    };
}
