using System.Diagnostics;

namespace OurTaikoEdit.App.Export;

// Where a preview video's frames go to be encoded with its sound into the file asked for.
public abstract class VideoSink
{
    // Before the first frame: the video's size, whether the frames' bytes are RGBA (else BGRA),
    // and the sound (a WAV) to go with them.
    public abstract Task StartAsync(VideoExportOptions options, int width, int height, bool rgba, string audio, CancellationToken cancel);
    // A frame's pixels; the buffer is the caller's again once the next call has returned (two
    // buffers take turns). False when the encoder has stopped: FinishAsync says why.
    public abstract Task<bool> WriteAsync(byte[] pixels, int frame, CancellationToken cancel);
    // After the last frame: the file is written, or this throws VideoExportException.
    public abstract Task FinishAsync(CancellationToken cancel);
    // The export failed or was stopped: everything let go (the file is deleted by the caller).
    public abstract Task AbortAsync();
}

// The desktop: ffmpeg reads the frames through a pipe and the sound from the WAV (H.264 and AAC in
// an MP4, or, not encoded, raw RGB and PCM in a MOV).
public sealed class FfmpegSink : VideoSink
{
    Process? ffmpeg;
    Stream? input;
    Task<string>? errors;
    Task writing = Task.CompletedTask;

    public override async Task StartAsync(VideoExportOptions options, int width, int height, bool rgba, string audio, CancellationToken cancel)
    {
        if (!File.Exists(options.Ffmpeg)) throw new VideoExportException(Core.Localization.Loc.T("VideoNoFfmpeg"));
        string encoding = VideoEncodings.Normalize(options.Encoding);
        bool raw = encoding == VideoEncodings.None;
        string codec = raw ? "rawvideo" : await VideoExporter.VideoCodecAsync(options.Ffmpeg);
        var info = new ProcessStartInfo(options.Ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", rgba ? "rgba" : "bgra", "-video_size", $"{width}x{height}", "-framerate", options.Fps.ToString(), "-i", "-",
            "-i", audio,
            "-map", "0:v", "-map", "1:a",
        }) info.ArgumentList.Add(arg);
        // Not encoded: QuickTime's uncompressed RGB ("raw ") and 16-bit PCM, which editors read.
        string[] video = raw ? new[] { "-c:v", "rawvideo", "-pix_fmt", "rgb24" }
            : codec == "libx264" ? new[] { "-c:v", "libx264", "-preset", encoding, "-crf", "18", "-pix_fmt", "yuv420p" }
            : new[] { "-c:v", codec, "-q:v", "2", "-pix_fmt", "yuv420p" };
        string[] sound = raw ? new[] { "-c:a", "pcm_s16le" } : new[] { "-c:a", "aac", "-b:a", "192k" };
        foreach (string arg in video.Concat(sound).Concat(new[] { "-shortest", "-movflags", "+faststart", options.Output }))
            info.ArgumentList.Add(arg);
        ffmpeg = Process.Start(info) ?? throw new VideoExportException("ffmpeg did not start.");
        errors = ffmpeg.StandardError.ReadToEndAsync();
        _ = ffmpeg.StandardOutput.ReadToEndAsync();
        input = ffmpeg.StandardInput.BaseStream;
    }

    public override async Task<bool> WriteAsync(byte[] pixels, int frame, CancellationToken cancel)
    {
        // The other buffer's write (the frame before) has to have ended before this one starts.
        try { await writing; }
        catch (IOException) { return false; }
        writing = input!.WriteAsync(pixels, cancel).AsTask();
        return true;
    }

    public override async Task FinishAsync(CancellationToken cancel)
    {
        try { await writing; } catch (IOException) { }
        try { input!.Close(); } catch (IOException) { }
        await ffmpeg!.WaitForExitAsync(cancel);
        if (ffmpeg.ExitCode != 0)
        {
            string said = (await errors!).Trim();
            if (said.Length > 800) said = "…" + said[^800..];
            throw new VideoExportException($"ffmpeg ({ffmpeg.ExitCode}): {said}");
        }
        ffmpeg.Dispose();
    }

    public override async Task AbortAsync()
    {
        if (ffmpeg == null) return;
        try { if (!ffmpeg.HasExited) ffmpeg.Kill(); ffmpeg.WaitForExit(2000); } catch (InvalidOperationException) { }
        // A write still going holds a buffer: it ends once ffmpeg is gone.
        try { await writing; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        ffmpeg.Dispose();
    }
}
