using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CharTaiko.UiTests.TestApp))]

namespace CharTaiko.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<CharTaiko.App.App>()
            .UseSkia()
            .With(CharTaiko.App.Localization.Fonts.Options("zh"))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
