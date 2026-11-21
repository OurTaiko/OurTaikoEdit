namespace OurTaikoEdit.Core.Transcoding;

// What a file can be converted to (after the transcoder at ~/Repos/transcoder, with the native
// ffmpeg's encoders): its extension, whether it carries a picture, the encoders it takes (the
// first is the default), and the source codecs that can be copied into it as they are ("*": any).
public sealed record TranscodeFormat(string Id, string Label, string Extension, bool IsVideo,
    IReadOnlyList<string> AudioCodecs, IReadOnlyList<string> AudioCopyFrom,
    IReadOnlyList<string> VideoCodecs, IReadOnlyList<string> VideoCopyFrom)
{
    // ffmpeg.wasm (a browser) rather than a native ffmpeg: its libopus breaks on stereo and VP9 is
    // too slow there (the transcoder's findings), so Opus is ffmpeg's own encoder and WebM is VP8.
    public static bool Wasm { get; set; }

    public static IReadOnlyList<TranscodeFormat> All => Wasm ? WasmFormats : NativeFormats;

    static readonly TranscodeFormat[] WasmFormats =
    {
        Audio("mp3", "MP3", "mp3", new[] { "libmp3lame" }, new[] { "mp3" }),
        Audio("m4a", "M4A (AAC)", "m4a", new[] { "aac" }, new[] { "aac" }),
        Audio("opus", "Opus", "opus", new[] { "opus" }, new[] { "opus" }),
        Audio("ogg", "OGG (Vorbis)", "ogg", new[] { "libvorbis" }, new[] { "vorbis" }),
        Audio("flac", "FLAC", "flac", new[] { "flac" }, new[] { "flac" }),
        Audio("wav", "WAV", "wav", new[] { "pcm" }, Array.Empty<string>()),
        Audio("alac", "ALAC (m4a)", "m4a", new[] { "alac" }, new[] { "alac" }),
        new("mp4", "MP4 (H.264)", "mp4", true, new[] { "aac", "libmp3lame" }, new[] { "aac", "mp3" }, new[] { "libx264" }, new[] { "h264", "hevc" }),
        new("webm", "WebM (VP8)", "webm", true, new[] { "opus", "libvorbis" }, new[] { "opus", "vorbis" }, new[] { "libvpx" }, new[] { "vp8", "vp9" }),
        new("mkv", "MKV", "mkv", true, new[] { "aac", "opus", "flac", "libmp3lame" }, new[] { "*" }, new[] { "libx264", "libvpx" }, new[] { "*" }),
        new("mov", "MOV", "mov", true, new[] { "aac", "alac" }, new[] { "aac", "alac" }, new[] { "libx264" }, new[] { "h264", "hevc", "prores" }),
    };

    static readonly TranscodeFormat[] NativeFormats =
    {
        Audio("mp3", "MP3", "mp3", new[] { "libmp3lame" }, new[] { "mp3" }),
        Audio("m4a", "M4A (AAC)", "m4a", new[] { "aac" }, new[] { "aac" }),
        Audio("opus", "Opus", "opus", new[] { "libopus" }, new[] { "opus" }),
        Audio("ogg", "OGG (Vorbis)", "ogg", new[] { "libvorbis" }, new[] { "vorbis" }),
        Audio("flac", "FLAC", "flac", new[] { "flac" }, new[] { "flac" }),
        Audio("wav", "WAV", "wav", new[] { "pcm" }, Array.Empty<string>()),
        Audio("alac", "ALAC (m4a)", "m4a", new[] { "alac" }, new[] { "alac" }),
        new("mp4", "MP4 (H.264)", "mp4", true, new[] { "aac", "libmp3lame" }, new[] { "aac", "mp3" }, new[] { "libx264" }, new[] { "h264", "hevc" }),
        new("webm", "WebM (VP9)", "webm", true, new[] { "libopus", "libvorbis" }, new[] { "opus", "vorbis" }, new[] { "libvpx-vp9", "libvpx" }, new[] { "vp8", "vp9", "av1" }),
        new("mkv", "MKV", "mkv", true, new[] { "aac", "libopus", "flac", "libmp3lame" }, new[] { "*" }, new[] { "libx264", "libvpx-vp9" }, new[] { "*" }),
        new("mov", "MOV", "mov", true, new[] { "aac", "alac" }, new[] { "aac", "alac" }, new[] { "libx264" }, new[] { "h264", "hevc", "prores" }),
    };

    static TranscodeFormat Audio(string id, string label, string ext, string[] codecs, string[] copy)
        => new(id, label, ext, false, codecs, copy, Array.Empty<string>(), Array.Empty<string>());

    public static TranscodeFormat Find(string? id) => All.FirstOrDefault(f => f.Id == id) ?? All[0];

    public bool CanCopyAudio(string codec) => AudioCopyFrom.Contains("*") || AudioCopyFrom.Contains(codec);
    public bool CanCopyVideo(string codec) => VideoCopyFrom.Contains("*") || VideoCopyFrom.Contains(codec);
}

// An encoder's choices: CBR bitrates (kbps) and VBR qualities (value, what it is about).
public static class Codecs
{
    public static string Name(string codec) => codec switch
    {
        "libmp3lame" => "MP3 (LAME)",
        "aac" => "AAC",
        "libopus" or "opus" => "Opus",
        "libvorbis" => "Vorbis",
        "flac" => "FLAC",
        "pcm" => "PCM",
        "alac" => "ALAC",
        "libx264" => "H.264 (x264)",
        "libvpx" => "VP8",
        "libvpx-vp9" => "VP9",
        _ => codec.ToUpperInvariant(),
    };

    public static IReadOnlyList<int>? Bitrates(string codec) => codec switch
    {
        "libmp3lame" => new[] { 64, 96, 128, 160, 192, 224, 256, 320 },
        "aac" => new[] { 64, 96, 128, 160, 192, 256, 320 },
        "libopus" or "opus" => new[] { 32, 48, 64, 96, 128, 160, 192, 256 },
        "libvorbis" => new[] { 64, 96, 128, 160, 192, 256, 320 },
        _ => null,
    };

    public static int DefaultBitrate(string codec) => codec switch { "libopus" or "opus" => 128, "libvorbis" => 160, _ => 192 };

    public static IReadOnlyList<(int Value, string Label)>? Qualities(string codec) => codec switch
    {
        "libmp3lame" => new[] { (0, "V0 ≈245k"), (1, "V1 ≈225k"), (2, "V2 ≈190k"), (3, "V3 ≈175k"), (4, "V4 ≈165k"), (5, "V5 ≈130k"), (6, "V6 ≈115k"), (7, "V7 ≈100k") },
        "libvorbis" => new[] { (10, "10 ≈500k"), (8, "8 ≈256k"), (6, "6 ≈192k"), (5, "5 ≈160k"), (4, "4 ≈128k"), (3, "3 ≈112k"), (2, "2 ≈96k") },
        _ => null,
    };

    public static int DefaultQuality(string codec) => codec == "libvorbis" ? 5 : 2;

    // VBR by default where the encoder has qualities (LAME's V2 and Vorbis' q5 are the usual picks).
    public static bool DefaultVbr(string codec) => Qualities(codec) != null;

    public static int DefaultCrf(string codec) => codec switch { "libvpx" => 10, "libvpx-vp9" => 32, _ => 23 };
    public static int MaxCrf(string codec) => codec.StartsWith("libvpx", StringComparison.Ordinal) ? 63 : 51;

    public static readonly string[] PcmDepths = { "pcm_s16le", "pcm_s24le", "pcm_f32le" };
    public static readonly int[] SampleRates = { 8000, 16000, 22050, 32000, 44100, 48000, 96000 };
    public static readonly int[] Heights = { 2160, 1440, 1080, 720, 480, 360 };
    public static readonly int[] FrameRates = { 60, 30, 25, 24, 15 };
    public static readonly string[] Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
}
