using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OurTaikoEdit.Core.Transcoding;

// Runs ffprobe and ffmpeg for the transcoder: what a file holds, a conversion with its progress,
// and a track decoded to samples (to hear and to draw).
public static class FfmpegRunner
{
    public static async Task<MediaInfo> ProbeAsync(string ffprobe, string file, CancellationToken cancel = default)
    {
        var (code, output, error) = await RunAsync(ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file }, cancel);
        if (code != 0) throw new TranscodeException(LastLines(error));
        try { return MediaInfo.FromProbe(output); }
        catch (System.Text.Json.JsonException e) { throw new TranscodeException(e.Message); }
    }

    // A conversion: progress goes from 0 to 1 over `duration` seconds of output (ffmpeg's
    // -progress). A failure throws with what ffmpeg said last; a cancel kills it and throws
    // OperationCanceledException. The output is deleted after either.
    public static async Task ConvertAsync(string ffmpeg, IReadOnlyList<string> args, string output, double duration, IProgress<double>? progress, CancellationToken cancel)
    {
        var all = new List<string> { "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);
        var info = Start(ffmpeg, all);
        using var process = Process.Start(info) ?? throw new TranscodeException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        bool ok = false;
        try
        {
            using var _ = cancel.Register(() => Kill(process));
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                // out_time_us (out_time_ms is also microseconds, despite its name).
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && duration > 0)
                    progress?.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
                else if (line == "progress=end") progress?.Report(1);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            cancel.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new TranscodeException(LastLines(await error));
            ok = true;
        }
        finally
        {
            if (!ok) { Kill(process); try { File.Delete(output); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
    }

    // One audio track as interleaved float samples at the rate and channels asked for, from
    // `start` for `length` seconds (null: to the end).
    public static async Task<float[]> DecodeAsync(string ffmpeg, string file, int audioIndex, int rate, int channels, CancellationToken cancel = default,
        double start = 0, double? length = null)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (start > 0) args.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", file });
        if (length is { } l) args.AddRange(new[] { "-t", l.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-map", $"0:a:{audioIndex}", "-vn", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-c:a", "pcm_f32le", "pipe:1" });
        using var process = Process.Start(Start(ffmpeg, args)) ?? throw new TranscodeException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var _ = cancel.Register(() => Kill(process));
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes, CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        cancel.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new TranscodeException(LastLines(await error));
        var samples = new float[bytes.Length / 4];
        MemoryMarshal.Cast<byte, float>(bytes.GetBuffer().AsSpan(0, samples.Length * 4)).CopyTo(samples);
        return samples;
    }

    static ProcessStartInfo Start(string program, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        return info;
    }

    static async Task<(int Code, string Output, string Error)> RunAsync(string program, IEnumerable<string> args, CancellationToken cancel)
    {
        try
        {
            using var process = Process.Start(Start(program, args)) ?? throw new TranscodeException($"{Path.GetFileName(program)} did not start.");
            using var _ = cancel.Register(() => Kill(process));
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            string output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            cancel.ThrowIfCancellationRequested();
            return (process.ExitCode, output, await error);
        }
        catch (System.ComponentModel.Win32Exception e) { throw new TranscodeException(e.Message); }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    // What ffmpeg said last, which is where it says what went wrong.
    static string LastLines(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "ffmpeg failed." : string.Join("\n", lines.TakeLast(3));
    }
}

public sealed class TranscodeException(string message) : Exception(message);
