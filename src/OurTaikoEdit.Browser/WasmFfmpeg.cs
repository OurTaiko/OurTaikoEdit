using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using OurTaikoEdit.Core.Transcoding;

namespace OurTaikoEdit.Browser;

// ffmpeg.wasm (ffmpeg-bridge.js) as the transcoder's engine: ffmpeg's own arguments, with the
// files' paths changed for names in ffmpeg's file system; what it holds is read from what
// `ffmpeg -i` says (there is no ffprobe).
sealed partial class WasmFfmpeg : IFfmpegEngine
{
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken cancel = default)
    {
        string name = "probe" + Path.GetExtension(file);
        var (_, log) = await RunAsync(new[] { (file, name) }, new[] { "-hide_banner", "-i", name }, null, null, cancel);
        var info = MediaInfo.FromFfmpegLog(log);
        if (!log.Contains("Input #0")) throw new TranscodeException(LastLines(log));
        return info;
    }

    public async Task ConvertAsync(IReadOnlyList<string> args, string output, double duration, IProgress<double>? progress, CancellationToken cancel)
    {
        var inputs = new List<(string Path, string Name)>();
        var mapped = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0 && args[i - 1] == "-i")
            {
                string name = $"in{inputs.Count}{Path.GetExtension(args[i])}";
                inputs.Add((args[i], name));
                mapped.Add(name);
            }
            else if (i == args.Count - 1 && args[i] == output) mapped.Add("out" + Path.GetExtension(output));
            else mapped.Add(args[i]);
        }
        bool ok = false;
        try
        {
            var (code, log) = await RunAsync(inputs, mapped, ("out" + Path.GetExtension(output), output),
                duration > 0 && progress != null ? t => progress.Report(Math.Clamp(t / duration, 0, 1)) : null, cancel);
            if (code != 0) throw new TranscodeException(LastLines(log));
            progress?.Report(1);
            ok = true;
        }
        finally
        {
            if (!ok) try { File.Delete(output); } catch (IOException) { }
        }
    }

    public async Task<float[]> DecodeAsync(string file, int audioIndex, int rate, int channels, CancellationToken cancel = default)
    {
        string raw = Path.Combine(Path.GetTempPath(), $"decode-{Guid.NewGuid():N}.raw");
        string name = "decode" + Path.GetExtension(file);
        try
        {
            var (code, log) = await RunAsync(new[] { (file, name) },
                new[] { "-hide_banner", "-v", "error", "-i", name, "-map", $"0:a:{audioIndex}", "-vn", "-ac", channels.ToString(), "-ar", rate.ToString(), "-f", "f32le", "-c:a", "pcm_f32le", "out.raw" },
                ("out.raw", raw), null, cancel);
            if (code != 0) throw new TranscodeException(LastLines(log));
            byte[] bytes = await File.ReadAllBytesAsync(raw, cancel);
            var samples = new float[bytes.Length / 4];
            MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, samples.Length * 4)).CopyTo(samples);
            return samples;
        }
        finally
        {
            try { File.Delete(raw); } catch (IOException) { }
        }
    }

    // One ffmpeg run: its exit code and what it said. A cancel stops ffmpeg.wasm (it loads again
    // when next asked) and throws.
    public static async Task<(int Code, string Log)> RunAsync(IReadOnlyList<(string Path, string Name)> inputs, IReadOnlyList<string> args,
        (string Name, string Path)? output, Action<double>? time, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string inputsJson = JsonSerializer.Serialize(inputs.Select(i => new[] { i.Path, i.Name }));
        string outputJson = output is { } o ? JsonSerializer.Serialize(new[] { o.Name, o.Path }) : "";
        string result;
        using (cancel.Register(CancelJs))
        {
            try { result = await RunJs(inputsJson, JsonSerializer.Serialize(args), outputJson, time ?? (_ => { })); }
            catch (JSException) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
            catch (JSException e) { throw new TranscodeException(e.Message); }
        }
        cancel.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(result);
        return (doc.RootElement.GetProperty("code").GetInt32(), doc.RootElement.GetProperty("log").GetString() ?? "");
    }

    static string LastLines(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Aborted", StringComparison.Ordinal)).ToArray();
        return lines.Length == 0 ? "ffmpeg failed." : string.Join("\n", lines.TakeLast(3));
    }

    [JSImport("ffmpeg.run", "main.js")]
    private static partial Task<string> RunJs(string inputs, string args, string output,
        [JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> time);

    [JSImport("ffmpeg.cancel", "main.js")]
    private static partial void CancelJs();
}
