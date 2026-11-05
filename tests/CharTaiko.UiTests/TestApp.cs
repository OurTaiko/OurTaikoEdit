using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CharTaiko.UiTests.TestApp))]

namespace CharTaiko.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<CharTaiko.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
