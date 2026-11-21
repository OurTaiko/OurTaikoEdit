using OurTaikoEdit.Core.Ffmpeg;

namespace OurTaikoEdit.Core.Transcoding;

// What the transcoder asks of ffmpeg, wherever it runs: the program on the desktop
// (ProcessFfmpeg), ffmpeg.wasm in a browser.
public interface IFfmpegEngine
{
    // What the files hold.
    Task<MediaInfo> ProbeAsync(string file, CancellationToken cancel = default);
    // A conversion with ffmpeg's arguments (TranscodePlan.Arguments); progress from 0 to 1 over
    // `duration` seconds of output. A failure throws TranscodeException, a cancel
    // OperationCanceledException; the output is gone after either.
    Task ConvertAsync(IReadOnlyList<string> args, string output, double duration, IProgress<double>? progress, CancellationToken cancel);
    // One audio track as interleaved float samples (to hear and to draw).
    Task<float[]> DecodeAsync(string file, int audioIndex, int rate, int channels, CancellationToken cancel = default);
}

public sealed class ProcessFfmpeg(FfmpegLocation location) : IFfmpegEngine
{
    public FfmpegLocation Location => location;

    public Task<MediaInfo> ProbeAsync(string file, CancellationToken cancel = default)
        => location.Ffprobe is { } ffprobe ? FfmpegRunner.ProbeAsync(ffprobe, file, cancel)
            : throw new TranscodeException(Localization.Loc.T("FfprobeMissing"));

    public Task ConvertAsync(IReadOnlyList<string> args, string output, double duration, IProgress<double>? progress, CancellationToken cancel)
        => FfmpegRunner.ConvertAsync(location.Ffmpeg, args, output, duration, progress, cancel);

    public Task<float[]> DecodeAsync(string file, int audioIndex, int rate, int channels, CancellationToken cancel = default)
        => FfmpegRunner.DecodeAsync(location.Ffmpeg, file, audioIndex, rate, channels, cancel);
}
