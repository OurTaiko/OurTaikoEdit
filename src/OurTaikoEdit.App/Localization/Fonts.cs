using Avalonia.Media;

namespace OurTaikoEdit.App.Localization;

// Fonts for Chinese and Japanese text, tried before the system's own fallback so that a line keeps
// one font and its weight. Chinese and Japanese draw some of the same characters differently, so
// the interface language decides which comes first (it takes effect at the next start).
public static class Fonts
{
    static readonly string[] Chinese = { "PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Micro Hei" };
    static readonly string[] Japanese = { "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic UI", "Meiryo UI", "Noto Sans CJK JP", "Source Han Sans JP" };

    public static FontManagerOptions Options(string language)
    {
        var names = language == "ja" ? Japanese.Concat(Chinese) : Chinese.Concat(Japanese);
        return new FontManagerOptions
        {
            FontFallbacks = names.Select(n => new FontFallback { FontFamily = new FontFamily(n) }).ToArray(),
        };
    }
}
