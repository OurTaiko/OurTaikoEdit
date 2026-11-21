using System.Globalization;
using System.Text;

namespace OurTaikoEdit.Core.Transcoding;

// The choices of a conversion, for every file in it. "auto" for a codec copies the source's
// track as it is when the format takes it and nothing asks for it to change, else encodes with
// the format's first encoder; "copy" always copies.
public sealed record TranscodeOptions
{
    public const string Auto = "auto", Copy = "copy";

    public string Format { get; init; } = "ogg";
    public int AudioTrack { get; init; }
    public string AudioCodec { get; init; } = Auto;
    public bool? Vbr { get; init; }
    public int? Bitrate { get; init; }
    public int? Quality { get; init; }
    public int FlacLevel { get; init; } = 5;
    public string PcmDepth { get; init; } = "pcm_s16le";
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public double VolumeDb { get; init; }
    public bool Normalize { get; init; }
    public double FadeIn { get; init; }
    public double FadeOut { get; init; }
    public bool KeepMetadata { get; init; } = true;
    public string VideoCodec { get; init; } = Auto;
    public int? Crf { get; init; }
    public string Preset { get; init; } = "medium";
    public int? Height { get; init; }
    public int? Fps { get; init; }
    // More arguments, before the output, as a shell would split them.
    public string Extra { get; init; } = "";

    public TranscodeFormat TargetFormat => TranscodeFormat.Find(Format);

    // The encoder used when the track is encoded.
    public string EffectiveAudioCodec => AudioCodec is Auto or Copy || !TargetFormat.AudioCodecs.Contains(AudioCodec) ? TargetFormat.AudioCodecs[0] : AudioCodec;
    public string? EffectiveVideoCodec => !TargetFormat.IsVideo ? null
        : VideoCodec is Auto or Copy || !TargetFormat.VideoCodecs.Contains(VideoCodec) ? TargetFormat.VideoCodecs[0] : VideoCodec;

    public bool IsVbr => Codecs.Qualities(EffectiveAudioCodec) != null && (Codecs.Bitrates(EffectiveAudioCodec) == null || (Vbr ?? Codecs.DefaultVbr(EffectiveAudioCodec)));

    public bool AudioFiltersWanted => VolumeDb != 0 || Normalize || FadeIn > 0 || FadeOut > 0 || SampleRate != null || Channels != null;
    public bool VideoFiltersWanted => Height != null || Fps != null;
}

// A part of a file to convert: from Start to End (null: its end), in seconds.
public readonly record struct Trim(double Start, double? End)
{
    public static Trim Whole => new(0, null);
    public bool IsWhole => Start <= 0 && End == null;
}

// What happens to a file: which audio track (null when there is none it can use) and whether
// the audio and the picture are copied or encoded.
public sealed record TranscodeDecision(int AudioIndex, AudioTrack? Source, bool AudioCopy, bool VideoCopy);

public static class TranscodePlan
{
    public static TranscodeDecision Decide(MediaInfo info, TranscodeOptions options)
    {
        var format = options.TargetFormat;
        int index = Math.Clamp(options.AudioTrack, 0, Math.Max(0, info.Audio.Count - 1));
        if (index < info.Audio.Count && info.Audio[index].Unsupported) index = Math.Max(0, info.FirstUsableAudio);
        var source = index < info.Audio.Count && !info.Audio[index].Unsupported ? info.Audio[index] : null;
        bool audioCopy = options.AudioCodec == TranscodeOptions.Copy
            || (options.AudioCodec == TranscodeOptions.Auto && source != null && !options.AudioFiltersWanted && format.CanCopyAudio(source.Codec));
        bool videoCopy = format.IsVideo && (options.VideoCodec == TranscodeOptions.Copy
            || (options.VideoCodec == TranscodeOptions.Auto && info.Video != null && !options.VideoFiltersWanted && format.CanCopyVideo(info.Video.Codec)));
        return new TranscodeDecision(index, source, audioCopy, videoCopy);
    }

    // ffmpeg's arguments for one file (without the program, and without -progress: the runner adds it).
    public static List<string> Arguments(MediaInfo info, TranscodeOptions options, Trim trim, string input, string output)
    {
        var d = Decide(info, options);
        var format = options.TargetFormat;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        double start = Math.Max(0, trim.Start);
        if (start > 0) args.AddRange(new[] { "-ss", N(start) });
        args.AddRange(new[] { "-i", input });
        if (trim.End is { } end) args.AddRange(new[] { "-t", N(Math.Max(0, end - start)) });

        if (format.IsVideo) args.AddRange(new[] { "-map", "0:v:0?" });
        if (d.Source != null) args.AddRange(new[] { "-map", $"0:a:{d.AudioIndex}" });
        args.AddRange(new[] { "-map_metadata", options.KeepMetadata ? "0" : "-1" });

        if (format.IsVideo)
        {
            if (d.VideoCopy) args.AddRange(new[] { "-c:v", "copy" });
            else
            {
                string vc = options.EffectiveVideoCodec!;
                int crf = Math.Clamp(options.Crf ?? Codecs.DefaultCrf(vc), 0, Codecs.MaxCrf(vc));
                args.AddRange(new[] { "-c:v", vc });
                switch (vc)
                {
                    case "libx264":
                        args.AddRange(new[] { "-crf", I(crf), "-preset", Codecs.Presets.Contains(options.Preset) ? options.Preset : "medium", "-pix_fmt", "yuv420p" });
                        break;
                    case "libvpx":
                        args.AddRange(new[] { "-crf", I(crf), "-b:v", "2M", "-deadline", "good", "-cpu-used", "4" });
                        break;
                    case "libvpx-vp9":
                        args.AddRange(new[] { "-crf", I(crf), "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-pix_fmt", "yuv420p" });
                        break;
                }
                if (options.Height is { } h) args.AddRange(new[] { "-vf", $"scale=-2:{I(h)}" });
                if (options.Fps is { } fps) args.AddRange(new[] { "-r", I(fps) });
            }
            if (format.Extension is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
        }

        if (d.Source != null)
        {
            if (d.AudioCopy) args.AddRange(new[] { "-c:a", "copy" });
            else
            {
                string c = options.EffectiveAudioCodec;
                args.AddRange(new[] { "-c:a", c == "pcm" ? (Codecs.PcmDepths.Contains(options.PcmDepth) ? options.PcmDepth : "pcm_s16le") : c });
                if (options.IsVbr)
                {
                    var qualities = Codecs.Qualities(c)!;
                    int q = options.Quality is { } wanted && qualities.Any(x => x.Value == wanted) ? wanted : Codecs.DefaultQuality(c);
                    args.AddRange(new[] { "-q:a", I(q) });
                }
                else if (Codecs.Bitrates(c) is { } rates)
                    args.AddRange(new[] { "-b:a", I(options.Bitrate is { } b && rates.Contains(b) ? b : Codecs.DefaultBitrate(c)) + "k" });
                if (c == "flac") args.AddRange(new[] { "-compression_level", I(Math.Clamp(options.FlacLevel, 0, 12)) });
                if (c == "libmp3lame" && !format.IsVideo) args.AddRange(new[] { "-id3v2_version", "3" });
                // Opus is always 48 kHz (ffmpeg resamples to it on its own).
                if (options.SampleRate is { } rate && c != "libopus") args.AddRange(new[] { "-ar", I(rate) });
                if (options.Channels is { } ch) args.AddRange(new[] { "-ac", I(ch) });
                var filters = new List<string>();
                if (options.VolumeDb != 0) filters.Add($"volume={N(options.VolumeDb)}dB");
                // loudnorm works at 192 kHz and leaves it so: back to the rate asked for (or the
                // source's). After a seek it gives out all it held as one frame, more than FLAC can
                // take in one: split into frames of 4096 (the last one not padded).
                if (options.Normalize)
                {
                    int back = c == "libopus" ? 48000 : options.SampleRate ?? d.Source.SampleRate ?? 48000;
                    filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
                    filters.Add($"aresample={I(back)}");
                    filters.Add("asetnsamples=n=4096:p=0");
                }
                if (options.FadeIn > 0) filters.Add($"afade=t=in:st=0:d={N(options.FadeIn)}");
                if (options.FadeOut > 0)
                {
                    double total = (trim.End ?? info.Duration) - start;
                    if (double.IsFinite(total) && total > options.FadeOut) filters.Add($"afade=t=out:st={N(total - options.FadeOut)}:d={N(options.FadeOut)}");
                }
                if (filters.Count > 0) args.AddRange(new[] { "-af", string.Join(",", filters) });
            }
        }
        args.AddRange(SplitArguments(options.Extra));
        args.Add(output);
        return args;
    }

    // The arguments as one line to read (and to paste into a shell).
    public static string CommandLine(IEnumerable<string> args) => "ffmpeg " + string.Join(" ", args.Select(Quote));

    static string Quote(string a)
    {
        if (a.Length > 0 && a.All(c => char.IsAsciiLetterOrDigit(c) || "._-:/=+,@%".Contains(c))) return a;
        return "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // Split as a shell would: spaces separate, quotes group, backslash escapes.
    public static List<string> SplitArguments(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < text.Length) current.Append(text[++i]);
                else current.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; inWord = true; }
            else if (c == '\\' && i + 1 < text.Length) { current.Append(text[++i]); inWord = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) { result.Add(current.ToString()); current.Clear(); inWord = false; }
            }
            else { current.Append(c); inWord = true; }
        }
        if (inWord) result.Add(current.ToString());
        return result;
    }

    // The output's file name: the input's name with the format's extension, in the folder; a
    // name that would overwrite the input (or another output of the batch) gets a number.
    public static string OutputPath(string input, string folder, TranscodeFormat format, ISet<string>? taken = null)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        string path = Path.Combine(folder, $"{name}.{format.Extension}");
        for (int n = 2; Same(path, input) || (taken?.Contains(path) ?? false); n++)
            path = Path.Combine(folder, $"{name} ({n}).{format.Extension}");
        taken?.Add(path);
        return path;

        static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
}
