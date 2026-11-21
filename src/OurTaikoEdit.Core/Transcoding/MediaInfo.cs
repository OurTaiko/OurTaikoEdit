using System.Globalization;
using System.Text.Json;

namespace OurTaikoEdit.Core.Transcoding;

// What ffprobe says about a file: its container, length, its audio tracks and its first picture
// track (cover art is not one).
public sealed record MediaInfo(string Container, double Duration, long? BitRate, IReadOnlyList<AudioTrack> Audio, VideoTrack? Video)
{
    // The first audio track this ffmpeg can decode; -1 when none can.
    public int FirstUsableAudio
    {
        get
        {
            for (int i = 0; i < Audio.Count; i++) if (!Audio[i].Unsupported) return i;
            return -1;
        }
    }

    // `ffprobe -v error -print_format json -show_format -show_streams <file>`.
    public static MediaInfo FromProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string container = "";
        double duration = double.NaN;
        long? bitRate = null;
        if (root.TryGetProperty("format", out var format))
        {
            container = Str(format, "format_name") ?? "";
            duration = Num(format, "duration") ?? double.NaN;
            bitRate = (long?)Num(format, "bit_rate");
        }
        var audio = new List<AudioTrack>();
        VideoTrack? video = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type") ?? "";
                // A codec this ffmpeg cannot decode has no codec_name, only its tag (e.g. APAC).
                string? codec = Str(s, "codec_name");
                string tag = Str(s, "codec_tag_string") is { } t && !t.StartsWith("[", StringComparison.Ordinal) ? t : "unknown";
                bool isDefault = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == 1;
                bool attached = s.TryGetProperty("disposition", out disp) && disp.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;
                string language = s.TryGetProperty("tags", out var tags) && Str(tags, "language") is { } l && l != "und" ? l : "";
                if (type == "audio")
                    audio.Add(new AudioTrack(codec ?? tag.ToLowerInvariant(), codec == null, language, (int?)Num(s, "sample_rate"), (int?)Num(s, "channels") ?? 0,
                        Str(s, "channel_layout") ?? "", (long?)Num(s, "bit_rate"), isDefault));
                else if (type == "video" && !attached && video == null)
                    video = new VideoTrack(codec ?? tag.ToLowerInvariant(), (int?)Num(s, "width") ?? 0, (int?)Num(s, "height") ?? 0, Rate(Str(s, "avg_frame_rate")) ?? Rate(Str(s, "r_frame_rate")));
            }
        }
        return new MediaInfo(container, duration, bitRate, audio, video);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ffprobe writes most numbers as strings.
    static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null;
    }

    static double? Rate(string? fraction)
    {
        if (fraction == null) return null;
        var parts = fraction.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0 && n > 0)
            return Math.Round(n / d, 3);
        return null;
    }
}

public sealed record AudioTrack(string Codec, bool Unsupported, string Language, int? SampleRate, int Channels, string Layout, long? BitRate, bool IsDefault);

public sealed record VideoTrack(string Codec, int Width, int Height, double? Fps);
