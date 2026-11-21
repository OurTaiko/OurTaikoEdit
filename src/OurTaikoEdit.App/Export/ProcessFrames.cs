using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OurTaikoEdit.App.Export;

// A video's frames drawn by copies of the app (child processes, started with --draw-frames): Avalonia
// draws only on its UI thread, one to a process, so to draw on more cores there are more processes.
// Each draws the pieces of the video it is given, without a window (Avalonia's headless platform with
// Skia), into its standard output; this one puts them in order and hands them to the sink (ffmpeg).
// The play is run up to a piece without drawing (VideoFrames), so the frames are the ones drawn in
// one go. The desktop's VideoExporter.DrawElsewhere.
//
// To a child (standard input, lines): the video (FrameRequest as JSON), then "draw <first> <count>".
// From it (standard output): a line "ok rgba", "ok bgra" or "error <why>", then each frame asked
// for, width × height × 4 bytes. It ends when its input does.
public static class ProcessFrames
{
    public const string Argument = "--draw-frames";
    // Frames to a piece, and pieces a worker may be ahead of the encoder.
    const int Piece = 4, Ahead = 2;

    // How a copy of the app is started (the program itself, or dotnet with the app's assembly).
    public static Func<ProcessStartInfo> Command { get; set; } = () =>
    {
        string program = Environment.ProcessPath ?? "dotnet";
        var info = new ProcessStartInfo(program);
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(ProcessFrames).Assembly.Location);
        return info;
    };

    // ---- The child ----

    // Avalonia is set up (headless) by Program before this: frames drawn as asked until the input ends.
    public static void Child()
    {
        using var busy = BusyActivity.Begin("Drawing a video's frames");
        var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = Console.OpenStandardOutput();
        void Say(string line) { output.Write(Encoding.UTF8.GetBytes(line + "\n")); output.Flush(); }
        FrameRequest? request;
        try { request = JsonSerializer.Deserialize(input.ReadLine() ?? "", FrameJson.Default.FrameRequest); }
        catch (JsonException e) { Say("error " + e.Message); return; }
        if (request == null) { Say("error No video."); return; }
        var (frames, error) = request.Open();
        if (frames == null) { Say("error " + error); return; }
        Say(frames.Rgba ? "ok rgba" : "ok bgra");
        var pixels = new byte[request.Width * request.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            while (input.ReadLine() is { } line)
            {
                var words = line.Split(' ');
                if (words.Length != 3 || words[0] != "draw" || !int.TryParse(words[1], out int first) || !int.TryParse(words[2], out int count)) continue;
                for (int f = first; f < first + count; f++)
                {
                    // Frames are drawn forward; one before the last drawn starts the play anew.
                    if (frames.Next > f) { frames.Dispose(); frames = request.Open().Frames!; }
                    frames.SkipTo(f);
                    frames.Draw(handle.AddrOfPinnedObject(), pixels.Length);
                    output.Write(pixels);
                }
                output.Flush();
            }
        }
        catch (IOException) { }   // this one went away
        finally
        {
            handle.Free();
            frames.Dispose();
        }
    }

    // ---- This one ----

    sealed class Worker
    {
        public required Process Process { get; init; }
        public required Stream Input { get; init; }
        public required Stream Output { get; init; }
        public required Task<string> Errors { get; init; }
        public bool Rgba;

        public async Task SendAsync(string line, CancellationToken cancel)
        {
            await Input.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel);
            await Input.FlushAsync(cancel);
        }

        public async Task<string> ReadLineAsync(CancellationToken cancel)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (await Output.ReadAsync(one, cancel) == 1 && one[0] != '\n') bytes.Add(one[0]);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Stop()
        {
            try { Input.Close(); } catch (IOException) { }
            try { if (!Process.HasExited) Process.Kill(); } catch (InvalidOperationException) { }
            Process.Dispose();
        }
    }

    static Worker Start(string request)
    {
        var info = Command();
        info.ArgumentList.Add(Argument);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        var process = Process.Start(info) ?? throw new IOException("The app could not be started again.");
        var worker = new Worker
        {
            Process = process, Input = process.StandardInput.BaseStream, Output = process.StandardOutput.BaseStream,
            Errors = process.StandardError.ReadToEndAsync(),
        };
        return worker;
    }

    // True when every frame went to the sink (or it stopped taking them); false when the workers did
    // not start, before any frame: the frames are drawn here then.
    public static async Task<bool> RunAsync(FrameJob job)
    {
        int count = job.Options.Workers;
        if (count < 2 || FrameRequest.Of(job) is not { } request) return false;
        string json = JsonSerializer.Serialize(request, FrameJson.Default.FrameRequest);
        int size = job.Width * job.Height * 4, frames = job.Frames;
        var cancel = job.Cancel;
        var workers = new List<Worker>();
        try
        {
            try
            {
                for (int i = 0; i < count; i++) workers.Add(Start(json));
                foreach (var w in workers) await w.SendAsync(json, cancel);
                foreach (var w in workers)
                {
                    string said = await w.ReadLineAsync(cancel);
                    if (!said.StartsWith("ok ")) throw new IOException(said.StartsWith("error ") ? said[6..] : (await w.Errors).Trim());
                    w.Rgba = said == "ok rgba";
                }
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"The video's frames are drawn here: {e.Message}");
                return false;
            }

            // Shared by the workers and the writer below, under `gate`: the frames drawn and not yet
            // written, the next piece to give, how many frames were written, and what went wrong.
            var gate = new object();
            var ready = new Dictionary<int, byte[]>();
            var spare = new Stack<byte[]>();
            int nextPiece = 0, written = 0;
            Exception? failed = null;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { var c = changed; changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); c.TrySetResult(); }
            async Task WaitAsync(Func<bool> until)
            {
                while (true)
                {
                    Task wait;
                    lock (gate)
                    {
                        if (failed != null || until()) return;
                        wait = changed.Task;
                    }
                    await wait.WaitAsync(cancel);
                }
            }

            async Task Draw(Worker w)
            {
                try
                {
                    while (true)
                    {
                        await WaitAsync(() => nextPiece * Piece >= frames || nextPiece * Piece - written < count * Piece * Ahead);
                        int first, n;
                        lock (gate)
                        {
                            if (failed != null || nextPiece * Piece >= frames) return;
                            first = nextPiece++ * Piece;
                            n = Math.Min(Piece, frames - first);
                        }
                        await w.SendAsync($"draw {first} {n}", cancel);
                        for (int f = first; f < first + n; f++)
                        {
                            byte[] pixels;
                            lock (gate) pixels = spare.Count > 0 ? spare.Pop() : new byte[size];
                            await w.Output.ReadExactlyAsync(pixels, cancel);
                            if (w.Rgba != job.Rgba) SwapRedBlue(pixels);
                            lock (gate) ready[f] = pixels;
                            Changed();
                        }
                    }
                }
                catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
                {
                    string why = (await w.Errors.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "")).Trim();
                    lock (gate) failed ??= new VideoExportException(why.Length > 0 ? why : "A copy drawing the frames stopped: " + e.Message);
                    Changed();
                }
            }

            var drawing = workers.Select(Draw).ToList();
            byte[]? previous = null;
            for (int f = 0; f < frames; f++)
            {
                await WaitAsync(() => ready.ContainsKey(f));
                byte[] pixels;
                lock (gate)
                {
                    if (failed != null) throw failed;
                    pixels = ready[f];
                    ready.Remove(f);
                }
                // False: the encoder stopped (FinishAsync says why).
                if (!await job.Sink.WriteAsync(pixels, f, cancel)) break;
                // The frame before is the sink's no more once this one is taken (VideoSink).
                lock (gate)
                {
                    if (previous != null) spare.Push(previous);
                    written = f + 1;
                }
                previous = pixels;
                Changed();
                job.Progress?.Report((f + 1, frames));
            }
            lock (gate) failed ??= new OperationCanceledException();   // the workers are done
            Changed();
            return true;
        }
        finally
        {
            foreach (var w in workers) w.Stop();
        }
    }

    static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}
