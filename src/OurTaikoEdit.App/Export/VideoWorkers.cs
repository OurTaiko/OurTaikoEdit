using System.Text.Json.Serialization;
using OurTaiko;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.App.Export;

// How many draw a video's frames at once: copies of the app that each draw pieces of it while this
// one puts them in order and encodes them (child processes on the desktop, ProcessFrames; Web Workers
// in a browser, where .NET has one thread). 1: drawn here, one after another.
public static class VideoWorkers
{
    // The most to choose from.
    public const int Most = 16;

    // The computer's cores and memory in MB (0: not known). A browser's host says them (from navigator).
    public static Func<(int Cores, double MemoryMb)> Machine { get; set; } =
        () => (Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0);

    // The number for a video of this size when none is chosen: the cores to spare (two are left for
    // the app and the encoder), at most 4, and no more than a quarter of the memory allows (1 GB when
    // it is not known): each is a .NET of its own, about 150 MB and 110 MB more per 1080p of frame
    // (263 MB drawing 1080p, measured in a browser; eight at 1080p ended the page).
    public static int Auto(int width, int height)
    {
        var (cores, memory) = Machine();
        double budget = (memory > 0 ? memory : 1024) / 4;
        double each = 150 + 110 * (width * (double)height) / (1920 * 1080);
        int n = Math.Min(Math.Min(4, cores - 2), (int)(budget / each));
        return Math.Max(1, n);
    }

    // The most the dialog offers: the cores (at least 2).
    public static int Choices => Math.Clamp(Machine().Cores, 2, Most);

    // A choice (0: Auto) as a number.
    public static int Resolve(int chosen, int width, int height) => chosen > 0 ? Math.Min(chosen, Most) : Auto(width, height);
}

// What a worker draws: the chart as written and how the play is set up, as VideoExportSource and
// VideoExportOptions have them (the sounds are not its).
public sealed record FrameRequest(string Text, int Block, string Title, string Subtitle, double Playhead, int Route, bool FromPlayhead,
    bool HitSounds, int Width, int Height, int Fps)
{
    // The job's video, or null when the chart as written is not known.
    public static FrameRequest? Of(FrameJob job) => job.Source.ChartText is { } text
        ? new FrameRequest(text, job.Source.ChartBlock, job.Source.Title, job.Source.Subtitle, job.Source.Playhead, (int)job.Source.Route,
            job.Options.FromPlayhead, job.Options.HitSounds, job.Width, job.Height, job.Options.Fps)
        : null;

    // The frames to draw, or why not.
    public (VideoFrames? Frames, string Error) Open()
    {
        var chart = new TjaLanguage().Analyze(Text, Block).Chart;
        if (chart == null) return (null, "The chart could not be read.");
        var silent = new PcmAudio { SampleRate = 48000, Channels = 1, Samples = [] };
        var source = new VideoExportSource(chart, Title, Subtitle, Playhead, (BranchRoute)Route, null, silent, silent, 0, 0);
        var options = new VideoExportOptions("", Width, Height, Fps, FromPlayhead, HitSounds, "");
        try { return (new VideoFrames(source, options, Width, Height), ""); }
        catch (VideoExportException e) { return (null, e.Message); }
    }
}

[JsonSerializable(typeof(FrameRequest))]
public sealed partial class FrameJson : JsonSerializerContext;
