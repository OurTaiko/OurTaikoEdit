using System.Runtime.InteropServices.JavaScript;
using OurTaikoEdit.App.Export;
using OurTaikoEdit.Core.Transcoding;

namespace OurTaikoEdit.Browser;

// A preview video in a browser: the frames encoded by the browser's own H.264 encoder (video.js),
// then ffmpeg.wasm puts the stream and the sound (AAC) in an MP4. ffmpeg.wasm only has the sound
// to encode, so it is quick. The encoding setting (x264's presets) has no say here.
sealed partial class WebCodecsSink : VideoSink
{
    int id = -1;
    VideoExportOptions? options;
    string audio = "";

    public override async Task StartAsync(VideoExportOptions options, int width, int height, bool rgba, string audio, CancellationToken cancel)
    {
        if (!SupportedJs()) throw new VideoExportException("This browser has no video encoder (WebCodecs).");
        this.options = options;
        this.audio = audio;
        try { id = await StartJs(width, height, options.Fps, rgba); }
        catch (JSException e) { throw new VideoExportException(e.Message); }
    }

    public override async Task<bool> WriteAsync(byte[] pixels, int frame, CancellationToken cancel)
    {
        if (!FrameJs(id, pixels, frame)) return false;
        await DrainJs(id);
        return true;
    }

    public override async Task FinishAsync(CancellationToken cancel)
    {
        string stream = Path.Combine(Path.GetTempPath(), $"video-{Guid.NewGuid():N}.h264");
        try
        {
            try { await FinishJs(id, stream); }
            catch (JSException e) { throw new VideoExportException(e.Message); }
            id = -1;
            var o = options!;
            var (code, log) = await WasmFfmpeg.RunAsync(new[] { (stream, "v.h264"), (audio, "a.wav") },
                new[] { "-hide_banner", "-loglevel", "error", "-framerate", o.Fps.ToString(), "-i", "v.h264", "-i", "a.wav",
                    "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", "out.mp4" },
                ("out.mp4", o.Output), null, cancel);
            if (code != 0) throw new VideoExportException($"ffmpeg ({code}): {log.Trim()}");
        }
        catch (TranscodeException e) { throw new VideoExportException(e.Message); }
        finally
        {
            try { File.Delete(stream); } catch (IOException) { }
        }
    }

    public override Task AbortAsync()
    {
        if (id >= 0) AbortJs(id);
        id = -1;
        return Task.CompletedTask;
    }

    [JSImport("video.supported", "main.js")]
    private static partial bool SupportedJs();

    [JSImport("video.start", "main.js")]
    private static partial Task<int> StartJs(int width, int height, int fps, bool rgba);

    [JSImport("video.frame", "main.js")]
    private static partial bool FrameJs(int id, [JSMarshalAs<JSType.MemoryView>] Span<byte> pixels, int index);

    [JSImport("video.drain", "main.js")]
    private static partial Task DrainJs(int id);

    [JSImport("video.finish", "main.js")]
    private static partial Task FinishJs(int id, string path);

    [JSImport("video.abort", "main.js")]
    private static partial void AbortJs(int id);
}
