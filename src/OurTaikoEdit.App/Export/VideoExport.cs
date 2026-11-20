using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OurTaiko;
using OurTaikoEdit.App.Controls;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Audio;

namespace OurTaikoEdit.App.Export;

// What the export dialog asks for: where the video goes, its size and frames a second, whether
// it starts at the playhead (as "play from here" does) or the beginning, whether the hit sounds
// are heard, and the ffmpeg that encodes it.
public sealed record VideoExportOptions(string Output, int Width, int Height, int Fps, bool FromPlayhead, bool HitSounds, string Ffmpeg);

// What is exported: the chart as the editor has it, and its sounds.
public sealed record VideoExportSource(TaikoChart Chart, string Title, string Subtitle, double Playhead, BranchRoute Route,
    PcmAudio? Song, PcmAudio Don, PcmAudio Ka, double SongVolume, double HitVolume);

// A preview video of the chart: the play scene (PlayStage) with auto play, as the play scene
// shows it, drawn frame by frame on the video's own clock rather than the screen's, with the
// song and the hit sounds mixed under it; ffmpeg encodes the two (H.264 and AAC in an MP4).
//
// The play is run twice on the same frame times: once without drawing, for the hit sounds
// (auto play hands them to its player, here one that writes them down), then again drawing each
// frame into the encoder. Both runs see the same clock, so they are the same play.
public static class VideoExporter
{
    // The time a play started for the video stands at, and the frame times it is run on.
    sealed class OfflinePlay
    {
        public required PlayViewModel Play { get; init; }
        public required RecordingPlayer Player { get; init; }
        public required Action<double> SetVideoTime { get; init; }
        public required double Begin { get; init; }
    }

    static OfflinePlay Start(VideoExportSource source, VideoExportOptions options)
    {
        double now = 0, begin = 0;
        double songLength = source.Song?.Duration ?? double.MaxValue;
        var player = new RecordingPlayer(() => begin + now, songLength);
        var (play, error) = PlayViewModel.Create(source.Chart, source.Title, source.Subtitle, options.FromPlayhead ? source.Playhead : null,
            source.Route, player, latency: 0, options.HitSounds, back: () => { }, seconds: () => now);
        if (play == null) throw new VideoExportException(error ?? "");
        begin = play.Time;
        play.Auto = true;
        return new OfflinePlay { Play = play, Player = player, SetVideoTime = t => now = t, Begin = begin };
    }

    // The hit sounds of the play, each at the song time it is heard at, and the frames it took.
    public static (List<RecordedHit> Hits, int Frames, double Begin) Rehearse(VideoExportSource source, VideoExportOptions options, CancellationToken cancel = default)
    {
        var p = Start(source, options);
        int frame = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            p.SetVideoTime((double)frame / options.Fps);
            p.Play.Tick();
            frame++;
            if (p.Play.Finished) break;
        }
        return (p.Player.Hits, frame, p.Begin);
    }

    // The video's sound: the song from where the video starts, and the hit sounds over it, at the
    // song's rate (48 kHz without a song), in stereo, as long as the video.
    public static PcmAudio Mix(VideoExportSource source, IReadOnlyList<RecordedHit> hits, double begin, double seconds)
    {
        int rate = source.Song?.SampleRate ?? 48000;
        long frames = (long)Math.Ceiling(seconds * rate);
        var mix = new float[frames * 2];
        if (source.Song is { } song)
        {
            int ch = Math.Max(1, song.Channels);
            long offset = (long)Math.Round(begin * rate);
            float gain = (float)source.SongVolume;
            for (long i = 0; i < frames; i++)
            {
                long at = i + offset;
                if (at < 0) continue;
                if (at >= song.Frames) break;
                float left = song.Samples[at * ch], right = ch > 1 ? song.Samples[at * ch + 1] : left;
                mix[i * 2] = left * gain;
                mix[i * 2 + 1] = right * gain;
            }
        }
        var don = Resample(source.Don, rate);
        var ka = Resample(source.Ka, rate);
        foreach (var hit in hits)
        {
            var sound = hit.Ka ? ka : don;
            // As the player has them: a small note's sound is a little quieter than a big one's.
            float gain = (float)((hit.Big ? 1.0 : 0.7) * source.HitVolume);
            long start = (long)Math.Round((hit.At - begin) * rate);
            for (int k = 0; k < sound.Length; k++)
            {
                long i = start + k;
                if (i < 0) continue;
                if (i >= frames) break;
                mix[i * 2] += sound[k] * gain;
                mix[i * 2 + 1] += sound[k] * gain;
            }
        }
        for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return new PcmAudio { SampleRate = rate, Channels = 2, Samples = mix };
    }

    // A sound as one channel at another rate (linear between samples).
    static float[] Resample(PcmAudio audio, int rate)
    {
        int ch = Math.Max(1, audio.Channels);
        long frames = audio.Frames;
        if (frames == 0) return Array.Empty<float>();
        long length = (long)Math.Ceiling(frames * (double)rate / audio.SampleRate);
        var result = new float[length];
        for (long i = 0; i < length; i++)
        {
            double at = i * (double)audio.SampleRate / rate;
            long a = (long)at;
            double f = at - a;
            float Sample(long n) { if (n >= frames) return 0; float s = 0; for (int c = 0; c < ch; c++) s += audio.Samples[n * ch + c]; return s / ch; }
            result[i] = (float)(Sample(a) * (1 - f) + Sample(a + 1) * f);
        }
        return result;
    }

    // ffmpeg: the one chosen, else the one found on the PATH or where package managers put it (an
    // app opened from the Finder does not get the shell's PATH); null when there is none.
    public static string? FindFfmpeg(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) return File.Exists(chosen.Trim()) ? chosen.Trim() : null;
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var folders = new List<string> { AppContext.BaseDirectory };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (!OperatingSystem.IsWindows()) folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/opt/local/bin", "/snap/bin" });
        foreach (string folder in folders)
        {
            try
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // The whole export: the play rehearsed for its sounds, the sound mixed, then every frame drawn
    // and handed to ffmpeg. Progress goes from 0 to 1 with the frames. A cancelled or failed
    // export leaves no video behind.
    public static async Task ExportAsync(VideoExportSource source, VideoExportOptions options, IProgress<(int Frame, int Frames)>? progress, CancellationToken cancel)
    {
        int width = options.Width & ~1, height = options.Height & ~1;
        if (width <= 0 || height <= 0 || options.Fps <= 0) throw new VideoExportException("Bad size or frame rate.");
        if (!File.Exists(options.Ffmpeg)) throw new VideoExportException(Core.Localization.Loc.T("VideoNoFfmpeg"));

        var (hits, frames, begin) = await Task.Run(() => Rehearse(source, options, cancel), cancel);
        string audio = Path.Combine(Path.GetTempPath(), $"ourtaikoedit-video-{Guid.NewGuid():N}.wav");
        try
        {
            await Task.Run(() => File.WriteAllBytes(audio, Mix(source, hits, begin, (double)frames / options.Fps).ToWav()), cancel);
            await EncodeAsync(source, options, width, height, frames, audio, progress, cancel);
        }
        finally
        {
            try { File.Delete(audio); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static async Task EncodeAsync(VideoExportSource source, VideoExportOptions options, int width, int height, int frames, string audio,
        IProgress<(int Frame, int Frames)>? progress, CancellationToken cancel)
    {
        var p = Start(source, options);
        var stage = new PlayStage { DataContext = p.Play };
        var size = new Size(width, height);
        stage.Measure(size);
        stage.Arrange(new Rect(size));
        using var target = new RenderTargetBitmap(new PixelSize(width, height));
        string pixelFormat = target.Format == PixelFormats.Rgba8888 ? "rgba" : "bgra";
        string codec = await VideoCodecAsync(options.Ffmpeg);

        var info = new ProcessStartInfo(options.Ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", pixelFormat, "-video_size", $"{width}x{height}", "-framerate", options.Fps.ToString(), "-i", "-",
            "-i", audio,
            "-map", "0:v", "-map", "1:a",
        }) info.ArgumentList.Add(arg);
        foreach (string arg in codec == "libx264"
            ? new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18" }
            : new[] { "-c:v", codec, "-q:v", "2" }) info.ArgumentList.Add(arg);
        foreach (string arg in new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", options.Output })
            info.ArgumentList.Add(arg);

        using var ffmpeg = Process.Start(info) ?? throw new VideoExportException("ffmpeg did not start.");
        var errors = ffmpeg.StandardError.ReadToEndAsync();
        _ = ffmpeg.StandardOutput.ReadToEndAsync();
        var input = ffmpeg.StandardInput.BaseStream;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        bool done = false;
        try
        {
            for (int frame = 0; frame < frames; frame++)
            {
                cancel.ThrowIfCancellationRequested();
                p.SetVideoTime((double)frame / options.Fps);
                p.Play.Tick();
                target.Render(stage);
                target.CopyPixels(new PixelRect(0, 0, width, height), pinned.AddrOfPinnedObject(), pixels.Length, stride);
                try { await input.WriteAsync(pixels, cancel); }
                catch (IOException) { break; } // ffmpeg stopped: what it says is told below.
                progress?.Report((frame + 1, frames));
                // The window is answered between frames (the frames are drawn on its thread).
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            try { input.Close(); } catch (IOException) { }
            await ffmpeg.WaitForExitAsync(cancel);
            if (ffmpeg.ExitCode != 0)
            {
                string said = (await errors).Trim();
                if (said.Length > 800) said = "…" + said[^800..];
                throw new VideoExportException($"ffmpeg ({ffmpeg.ExitCode}): {said}");
            }
            done = true;
        }
        finally
        {
            pinned.Free();
            if (!done)
            {
                try { if (!ffmpeg.HasExited) ffmpeg.Kill(); ffmpeg.WaitForExit(2000); } catch (InvalidOperationException) { }
                try { File.Delete(options.Output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // H.264 (libx264) where the ffmpeg has it, else MPEG-4 part 2, which every build has.
    static async Task<string> VideoCodecAsync(string ffmpeg)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-encoders");
            using var process = Process.Start(info);
            if (process == null) return "libx264";
            var error = process.StandardError.ReadToEndAsync();
            string list = await process.StandardOutput.ReadToEndAsync();
            await error;
            await process.WaitForExitAsync();
            return list.Contains(" libx264 ") ? "libx264" : "mpeg4";
        }
        catch (System.ComponentModel.Win32Exception) { return "libx264"; }
    }
}

public sealed class VideoExportException(string message) : Exception(message);

// A hit sound of the play, at the song time it is heard at.
public readonly record struct RecordedHit(double At, bool Ka, bool Big);

// The play's player for a video: no device; the song "plays" on the video's clock, and the hit
// sounds asked for are written down at the song time they would be heard at.
sealed class RecordingPlayer(Func<double> clock, double duration) : IAudioPlayer
{
    public List<RecordedHit> Hits { get; } = new();
    public double Duration => duration;
    public bool IsPlaying { get; private set; }
    public double Position => clock();
    public float Volume { get; set; } = 1;
    public float HitVolume { get; set; } = 1;
    public double OutputLatency => 0;
    public void Play() => IsPlaying = true;
    public void Pause() => IsPlaying = false;
    public void Seek(double seconds) { }
    public void PlayHit(bool ka, bool big) => Hits.Add(new RecordedHit(clock(), ka, big));
    public void PlayHitAt(double songTime, bool ka, bool big) => Hits.Add(new RecordedHit(songTime, ka, big));
    public void CancelHits() { }
    public void SetHitSounds(PcmAudio don, PcmAudio ka) { }
    public void Dispose() { }
}
