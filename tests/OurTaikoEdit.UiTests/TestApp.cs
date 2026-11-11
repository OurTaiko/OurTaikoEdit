using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(OurTaikoEdit.UiTests.TestApp))]

namespace OurTaikoEdit.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<OurTaikoEdit.App.App>()
            .UseSkia()
            .With(OurTaikoEdit.App.Localization.Fonts.Options("zh"))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
