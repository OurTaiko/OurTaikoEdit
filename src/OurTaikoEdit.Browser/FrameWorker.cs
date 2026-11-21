using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using OurTaikoEdit.App.Export;

namespace OurTaikoEdit.Browser;

// A video's frames drawn in a Web Worker (frame-worker.js): the same program, started there without
// its page (no Main, no DOM), with Avalonia's headless platform drawing with Skia into memory. The
// page hands each worker pieces of the video (ParallelFrames); the play is run up to a piece without
// drawing (VideoFrames), so the frames are the ones drawn in one go.
static partial class FrameWorker
{
    static bool started;
    static VideoFrames? frames;
    static FrameRequest? request;
    static byte[]? pixels;

    // Avalonia without a page: headless, drawing with Skia, with the app's fonts (and the CJK one,
    // as bytes from the page, which fetched it already).
    [JSExport]
    public static void Start(byte[]? cjkFont)
    {
        if (started) return;
        CjkFont.Use(cjkFont);
        App.App.Starting = CjkFont.Register;
        var builder = AppBuilder.Configure<App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
        if (CjkFont.HasFont)
            builder = builder.With(new FontManagerOptions { FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily(CjkFont.Family) } } });
        builder.SetupWithoutStarting();
        started = true;
    }

    // The video to draw (JSON, FrameRequest); "" when it can be, else why not. RGBA or BGRA
    // is said by Rgba().
    [JSExport]
    public static string Prepare(string json)
    {
        request = JsonSerializer.Deserialize(json, FrameJson.Default.FrameRequest)!;
        pixels = new byte[request.Width * request.Height * 4];
        frames?.Dispose();
        (frames, string error) = request.Open();
        return error;
    }

    [JSExport]
    public static bool Rgba() => frames!.Rgba;

    // A frame's pixels. Frames are drawn forward; one before the last drawn starts the play anew.
    [JSExport]
    public static byte[] Draw(int frame)
    {
        if (frames!.Next > frame)
        {
            frames.Dispose();
            frames = request!.Open().Frames!;
        }
        frames.SkipTo(frame);
        var handle = GCHandle.Alloc(pixels!, GCHandleType.Pinned);
        try { frames.Draw(handle.AddrOfPinnedObject(), pixels!.Length); }
        finally { handle.Free(); }
        return pixels!;
    }
}
