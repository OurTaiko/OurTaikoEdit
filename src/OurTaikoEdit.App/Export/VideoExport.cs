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
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App.Export;

// What the export dialog asks for: where the video goes, its size and frames a second, whether
// it starts at the playhead (as "play from here" does) or the beginning, whether the hit sounds
// are heard, the ffmpeg that writes it, how it is encoded: an x264 preset (VideoEncodings), or
// "none" for the frames as they are (an uncompressed MOV), and how many draw the frames at once
// (VideoWorkers; 1: drawn here).
public sealed record VideoExportOptions(string Output, int Width, int Height, int Fps, bool FromPlayhead, bool HitSounds, string Ffmpeg,
    string Encoding = VideoEncodings.Default, int Workers = 1);

// x264's presets, fastest first, and "none" (AppSettings.VideoEncodings). Slower ones make a
// smaller file of the same quality.
public static class VideoEncodings
{
    public const string Default = AppSettings.DefaultVideoEncoding, None = AppSettings.NoVideoEncoding;
    public static IReadOnlyList<string> All => AppSettings.VideoEncodings;
    public static string Normalize(string? encoding) => All.Contains(encoding) ? encoding! : Default;
    // The file's extension for an encoding: an MP4, or a MOV for frames not encoded.
    public static string Extension(string encoding) => encoding == None ? ".mov" : ".mp4";
}

// What is exported: the chart as the editor has it, and its sounds.
public sealed record VideoExportSource(TaikoChart Chart, string Title, string Subtitle, double Playhead, BranchRoute Route,
    PcmAudio? Song, PcmAudio Don, PcmAudio Ka, double SongVolume, double HitVolume, PcmAudio? BalloonPop = null, PcmAudio? KusudamaPop = null)
{
    // The chart as written, and its block (course), for a drawer that reads it anew (a Web Worker);
    // null when not known.
    public string? ChartText { get; init; }
    public int ChartBlock { get; init; }
}

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
    internal sealed class OfflinePlay
    {
        public required PlayViewModel Play { get; init; }
        public required RecordingPlayer Player { get; init; }
        public required Action<double> SetVideoTime { get; init; }
        public required double Begin { get; init; }
    }

    internal static OfflinePlay Start(VideoExportSource source, VideoExportOptions options)
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
        float[] none = [];
        var balloon = source.BalloonPop is { } balloonPop ? Resample(balloonPop, rate) : none;
        var kusudama = source.KusudamaPop is { } kusudamaPop ? Resample(kusudamaPop, rate) : none;
        foreach (var hit in hits)
        {
            var sound = hit.Pop switch { PopKind.Balloon => balloon, PopKind.Kusudama => kusudama, _ => hit.Ka ? ka : don };
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

    // ffmpeg: the one chosen, else the one the app downloaded (with `tools`), else one installed on
    // the system; null when there is none (see FfmpegTools.Find).
    public static string? FindFfmpeg(string? chosen, OurTaikoEdit.Core.Ffmpeg.FfmpegTools? tools = null)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) return File.Exists(chosen.Trim()) ? chosen.Trim() : null;
        return (tools?.Find(null) ?? OurTaikoEdit.Core.Ffmpeg.FfmpegTools.FindOnSystem())?.Ffmpeg;
    }

    // The whole export: the play rehearsed for its sounds, the sound mixed, then every frame drawn
    // and handed to ffmpeg. Progress goes from 0 to 1 with the frames. A cancelled or failed
    // export leaves no video behind.
    public static async Task ExportAsync(VideoExportSource source, VideoExportOptions options, IProgress<(int Frame, int Frames)>? progress, CancellationToken cancel)
    {
        int width = options.Width & ~1, height = options.Height & ~1;
        if (width <= 0 || height <= 0 || options.Fps <= 0) throw new VideoExportException("Bad size or frame rate.");

        // Not slowed by macOS while the person looks at another window (BusyActivity).
        using var busy = BusyActivity.Begin("Exporting a video");
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

    // Where the frames go: ffmpeg on the desktop; a browser's own encoder there (set by its host).
    public static Func<VideoSink> CreateSink { get; set; } = () => new FfmpegSink();

    // Frames drawn elsewhere (a browser's Web Workers) and handed to the sink there: true when they
    // were (all of them, in order), false to draw them here. Set by the host.
    public static Func<FrameJob, Task<bool>>? DrawElsewhere { get; set; }


    static async Task EncodeAsync(VideoExportSource source, VideoExportOptions options, int width, int height, int frames, string audio,
        IProgress<(int Frame, int Frames)>? progress, CancellationToken cancel)
    {
        using var drawn = new VideoFrames(source, options, width, height);
        bool rgba = drawn.Rgba;
        var sink = CreateSink();
        await sink.StartAsync(options, width, height, rgba, audio, cancel);
        if (DrawElsewhere is { } elsewhere)
        {
            bool drawnThere;
            try { drawnThere = await elsewhere(new FrameJob(source, options, width, height, frames, rgba, sink, progress, cancel)); }
            catch { await sink.AbortAsync(); throw; }
            if (drawnThere)
            {
                await sink.FinishAsync(cancel);
                return;
            }
        }
        int stride = width * 4;
        // Two frames' pixels: one is drawn while the sink takes the other (ffmpeg reads it through
        // its pipe only so fast, and drawing waiting for that was a quarter of the time).
        var pixels = new[] { new byte[stride * height], new byte[stride * height] };
        var pinned = pixels.Select(b => GCHandle.Alloc(b, GCHandleType.Pinned)).ToArray();
        var sinceAnswered = Stopwatch.StartNew();
        bool done = false;
        try
        {
            for (int frame = 0; frame < frames; frame++)
            {
                cancel.ThrowIfCancellationRequested();
                int b = frame % 2;
                drawn.Draw(pinned[b].AddrOfPinnedObject(), pixels[b].Length);
                // False: the encoder stopped (what it says is told by FinishAsync).
                if (!await sink.WriteAsync(pixels[b], frame, cancel)) break;
                progress?.Report((frame + 1, frames));
                // The window is answered between frames (the frames are drawn on its thread),
                // about once a screen frame.
                if (sinceAnswered.ElapsedMilliseconds >= 15)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    sinceAnswered.Restart();
                }
            }
            await sink.FinishAsync(cancel);
            done = true;
        }
        finally
        {
            if (!done)
            {
                await sink.AbortAsync();
                try { File.Delete(options.Output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var handle in pinned) handle.Free();
        }
    }

    // H.264 (libx264) where the ffmpeg has it, else MPEG-4 part 2, which every build has.
    internal static async Task<string> VideoCodecAsync(string ffmpeg)
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

// The frames of a video for a drawer elsewhere (VideoExporter.DrawElsewhere): what to draw, the
// sink, started, that they go to, and whether it takes them as RGBA (else BGRA).
public sealed record FrameJob(VideoExportSource Source, VideoExportOptions Options, int Width, int Height, int Frames, bool Rgba, VideoSink Sink,
    IProgress<(int Frame, int Frames)>? Progress, CancellationToken Cancel);

// The video's frames, drawn one after another from any frame on: the play is run up to that frame
// without drawing (its state is the play's alone, and running it costs next to nothing), so pieces
// of the video can be drawn apart (each in a Web Worker of its own, in a browser) and be the same
// frames as drawn in one go. On the UI thread (Avalonia draws there).
public sealed class VideoFrames : IDisposable
{
    readonly VideoExporter.OfflinePlay play;
    readonly PlayStage stage;
    readonly RenderTargetBitmap target;
    readonly int fps, width, height;

    public VideoFrames(VideoExportSource source, VideoExportOptions options, int width, int height)
    {
        play = VideoExporter.Start(source, options);
        (fps, this.width, this.height) = (options.Fps, width, height);
        stage = new PlayStage { DataContext = play.Play };
        var size = new Size(width, height);
        stage.Measure(size);
        stage.Arrange(new Rect(size));
        target = new RenderTargetBitmap(new PixelSize(width, height));
    }

    // Whether the pixels are RGBA (else BGRA).
    public bool Rgba => target.Format == PixelFormats.Rgba8888;
    // The frame Draw draws next.
    public int Next { get; private set; }

    void Advance()
    {
        play.SetVideoTime((double)Next / fps);
        play.Play.Tick();
        Next++;
    }

    // The play run on to `frame` (not back), nothing drawn.
    public void SkipTo(int frame)
    {
        while (Next < frame) Advance();
    }

    // The next frame drawn into `pixels` (width × height × 4 bytes).
    public void Draw(IntPtr pixels, int length)
    {
        Advance();
        target.Render(stage);
        target.CopyPixels(new PixelRect(0, 0, width, height), pixels, length, width * 4);
    }

    public void Dispose() => target.Dispose();
}

// A hit sound of the play, at the song time it is heard at; a pop is a balloon popping or a
// kusudama breaking (at the volume of a big note).
public readonly record struct RecordedHit(double At, bool Ka, bool Big, PopKind Pop = PopKind.None);

public enum PopKind { None, Balloon, Kusudama }

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
    public void PlayPop(bool kusudama) => Hits.Add(new RecordedHit(clock(), false, true, kusudama ? PopKind.Kusudama : PopKind.Balloon));
    public void PlayPopAt(double songTime, bool kusudama) => Hits.Add(new RecordedHit(songTime, false, true, kusudama ? PopKind.Kusudama : PopKind.Balloon));
    public void CancelHits() { }
    public void SetHitSounds(PcmAudio don, PcmAudio ka) { }
    public void Dispose() { }
}
