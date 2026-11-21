using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using OurTaikoEdit.App.Export;

namespace OurTaikoEdit.Browser;

// A video's frames drawn by Web Workers (FrameWorker, frame-worker.js), several at once, while the
// page encodes them in order (video.js): the page has one thread for .NET, and drawing a frame there
// was nine tenths of an export. VideoExporter.DrawElsewhere.
static partial class ParallelFrames
{
    public static async Task<bool> RunAsync(FrameJob job)
    {
        // One worker would only move the drawing off the page, at the memory of another .NET.
        int workers = job.Options.Workers;
        if (workers < 2 || FrameRequest.Of(job) is not { } frames || job.Sink is not WebCodecsSink sink) return false;
        string request = JsonSerializer.Serialize(frames, FrameJson.Default.FrameRequest);
        using var stop = job.Cancel.Register(() => AbortJs(sink.Id));
        string error = await RunJs(sink.Id, request, job.Frames, workers, new ArraySegment<byte>(CjkFont.Bytes ?? []),
            done => job.Progress?.Report((done, job.Frames)));
        job.Cancel.ThrowIfCancellationRequested();
        if (error.StartsWith("fallback: "))
        {
            Console.WriteLine($"The video's frames are drawn on the page: {error["fallback: ".Length..]}");
            return false;
        }
        if (error != "") throw new VideoExportException(error);
        return true;
    }

    // The computer's cores and memory in MB (0: the browser does not say), for VideoWorkers.
    public static (int Cores, double MemoryMb) Machine() => (CoresJs(), MemoryJs());

    [JSImport("video.cores", "main.js")]
    private static partial int CoresJs();

    [JSImport("video.memory", "main.js")]
    private static partial double MemoryJs();

    // "" when every frame was drawn and handed to the encoder; "fallback: …" when it failed before
    // any was (the workers did not start): the page draws them; else what went wrong.
    [JSImport("video.parallel", "main.js")]
    private static partial Task<string> RunJs(int id, string request, int frames, int workers,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> font, [JSMarshalAs<JSType.Function<JSType.Number>>] Action<int> progress);

    [JSImport("video.stopParallel", "main.js")]
    private static partial void AbortJs(int id);
}
