using OurTaikoEdit.Core.Ffmpeg;
using OurTaikoEdit.Core.Transcoding;

namespace OurTaikoEdit.Tests;

// The transcoder's reading of ffprobe, its choice of copying or encoding, and ffmpeg's arguments.
public class TranscodeTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-transcode-" + Guid.NewGuid().ToString("N"));

    public TranscodeTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    // An H.264 video with a Japanese AAC track and an APAC one this ffmpeg cannot decode, plus cover art.
    const string Probe = """
        { "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "codec_tag_string": "avc1", "width": 1920, "height": 1080,
              "r_frame_rate": "60/1", "avg_frame_rate": "60000/1001", "disposition": { "default": 1, "attached_pic": 0 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "codec_tag_string": "mp4a", "sample_rate": "48000", "channels": 2,
              "channel_layout": "stereo", "bit_rate": "192000", "disposition": { "default": 1 }, "tags": { "language": "jpn" } },
            { "index": 2, "codec_type": "audio", "codec_tag_string": "apac", "sample_rate": "48000", "channels": 4, "disposition": { "default": 0 } },
            { "index": 3, "codec_name": "mjpeg", "codec_type": "video", "codec_tag_string": "[0][0][0][0]", "width": 600, "height": 600,
              "disposition": { "attached_pic": 1 } } ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "125.500000", "bit_rate": "4000000" } }
        """;

    static MediaInfo Video => MediaInfo.FromProbe(Probe);

    static MediaInfo Song(string codec = "mp3") => new("mp3", 90, null, new[] { new AudioTrack(codec, false, "", 44100, 2, "stereo", 320000, true) }, null);

    [Fact]
    public void FfprobeIsRead()
    {
        var info = Video;
        Assert.Equal(125.5, info.Duration);
        Assert.Equal(4000000, info.BitRate);
        Assert.Equal(new VideoTrack("h264", 1920, 1080, 59.94), info.Video);
        Assert.Equal(2, info.Audio.Count);
        Assert.Equal(new AudioTrack("aac", false, "jpn", 48000, 2, "stereo", 192000, true), info.Audio[0]);
        Assert.True(info.Audio[1].Unsupported);
        Assert.Equal("apac", info.Audio[1].Codec);
    }

    // What `ffmpeg -i` says (ffmpeg.wasm has no ffprobe).
    [Fact]
    public void FfmpegsOwnDescriptionIsRead()
    {
        const string log = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'in0.mp4':
              Duration: 00:02:05.50, start: 0.000000, bitrate: 4000 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 3800 kb/s, 59.94 fps, 59.94 tbr, 60k tbn (default)
              Stream #0:1[0x2](jpn): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 192 kb/s (default)
              Stream #0:2[0x3](und): Audio: none (apac / 0x63617061), 48000 Hz, 4 channels
              Stream #0:3[0x4](und): Video: mjpeg (Baseline), yuvj420p(pc), 600x600, 90k tbr, 90k tbn (attached pic)
            At least one output file must be specified
            """;
        var info = MediaInfo.FromFfmpegLog(log);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", info.Container);
        Assert.Equal(125.5, info.Duration);
        Assert.Equal(4000000, info.BitRate);
        Assert.Equal(new VideoTrack("h264", 1920, 1080, 59.94), info.Video);
        Assert.Equal(new AudioTrack("aac", false, "jpn", 48000, 2, "stereo", 192000, true), info.Audio[0]);
        Assert.Equal(new AudioTrack("apac", true, "", 48000, 4, "4 channels", null, false), info.Audio[1]);
    }

    // ffmpeg.wasm: Opus is ffmpeg's own encoder (48 kHz, at most two channels), WebM is VP8 at its fastest.
    [Fact]
    public void TheBrowsersFfmpegGetsItsOwnEncoders()
    {
        TranscodeFormat.Wasm = true;
        try
        {
            var surround = new MediaInfo("x", 10, null, new[] { new AudioTrack("aac", false, "", 48000, 6, "5.1", null, true) }, null);
            string opus = string.Join(" ", TranscodePlan.Arguments(surround, new TranscodeOptions { Format = "opus" }, Trim.Whole, "in", "out"));
            Assert.Contains("-c:a opus -b:a 128k -strict -2 -ar 48000 -ac 2", opus);
            string webm = string.Join(" ", TranscodePlan.Arguments(Video, new TranscodeOptions { Format = "webm" }, Trim.Whole, "in", "out"));
            Assert.Contains("-c:v libvpx -crf 10 -b:v 2M -deadline realtime -cpu-used 8", webm);
            Assert.DoesNotContain(TranscodeFormat.All, f => f.VideoCodecs.Contains("libvpx-vp9") || f.AudioCodecs.Contains("libopus"));
        }
        finally { TranscodeFormat.Wasm = false; }
    }

    [Fact]
    public void ATrackThatFitsIsCopiedUnlessSomethingChangesIt()
    {
        Assert.True(TranscodePlan.Decide(Song("vorbis"), new TranscodeOptions { Format = "ogg" }).AudioCopy);
        Assert.False(TranscodePlan.Decide(Song("mp3"), new TranscodeOptions { Format = "ogg" }).AudioCopy);
        Assert.False(TranscodePlan.Decide(Song("vorbis"), new TranscodeOptions { Format = "ogg", VolumeDb = -3 }).AudioCopy);
        Assert.False(TranscodePlan.Decide(Song("vorbis"), new TranscodeOptions { Format = "ogg", AudioCodec = "libvorbis" }).AudioCopy);
        // WAV is always written as PCM.
        Assert.False(TranscodePlan.Decide(Song("pcm_s16le"), new TranscodeOptions { Format = "wav" }).AudioCopy);
        // MKV takes anything.
        var mkv = TranscodePlan.Decide(Video, new TranscodeOptions { Format = "mkv" });
        Assert.True(mkv.AudioCopy && mkv.VideoCopy);
        // A picture that would be scaled is encoded.
        Assert.False(TranscodePlan.Decide(Video, new TranscodeOptions { Format = "mp4", Height = 720 }).VideoCopy);
        Assert.False(TranscodePlan.Decide(Video, new TranscodeOptions { Format = "webm" }).VideoCopy);
    }

    [Fact]
    public void AnUndecodableTrackIsPassedOverForOneThatCanBe()
    {
        var d = TranscodePlan.Decide(Video, new TranscodeOptions { Format = "mp3", AudioTrack = 1 });
        Assert.Equal(0, d.AudioIndex);
        Assert.Equal("aac", d.Source!.Codec);
        var none = new MediaInfo("x", 10, null, new[] { Video.Audio[1] }, null);
        Assert.Null(TranscodePlan.Decide(none, new TranscodeOptions()).Source);
    }

    [Fact]
    public void SongToOggWithTrimFadeAndNormalizing()
    {
        var args = TranscodePlan.Arguments(Song(), new TranscodeOptions { Format = "ogg", Normalize = true, FadeOut = 2, VolumeDb = -1.5 }, new Trim(10, 70), "in.mp3", "out.ogg");
        Assert.Equal(
            "-hide_banner -nostdin -y -ss 10 -i in.mp3 -t 60 -map 0:a:0 -map_metadata 0 -c:a libvorbis -q:a 5 " +
            "-af volume=-1.5dB,loudnorm=I=-16:TP=-1.5:LRA=11,aresample=44100,asetnsamples=n=4096:p=0,afade=t=out:st=58:d=2 out.ogg",
            string.Join(" ", args));
    }

    [Fact]
    public void EncodersGetTheirOwnSettings()
    {
        string A(TranscodeOptions o, MediaInfo? info = null) => string.Join(" ", TranscodePlan.Arguments(info ?? Song(), o, Trim.Whole, "in", "out"));
        Assert.Contains("-c:a libmp3lame -q:a 2 -id3v2_version 3", A(new() { Format = "mp3", AudioCodec = "libmp3lame" }));
        Assert.Contains("-c:a libmp3lame -b:a 320k", A(new() { Format = "mp3", AudioCodec = "libmp3lame", Vbr = false, Bitrate = 320 }));
        Assert.Contains("-c:a aac -b:a 192k", A(new() { Format = "m4a" }));
        Assert.Contains("-c:a libopus -b:a 128k -ac 2", A(new() { Format = "opus", SampleRate = 44100, Channels = 2 }));
        Assert.DoesNotContain("-ar", A(new() { Format = "opus", SampleRate = 44100 }));
        Assert.Contains("-c:a flac -compression_level 8 -ar 48000", A(new() { Format = "flac", FlacLevel = 8, SampleRate = 48000 }));
        Assert.Contains("-c:a pcm_s24le", A(new() { Format = "wav", PcmDepth = "pcm_s24le" }));
        Assert.Contains("-map_metadata -1", A(new() { Format = "wav", KeepMetadata = false }));
        Assert.Contains("-c:v libx264 -crf 20 -preset slow -pix_fmt yuv420p -vf scale=-2:720 -r 30 -movflags +faststart -c:a copy",
            A(new() { Format = "mp4", Crf = 20, Preset = "slow", Height = 720, Fps = 30 }, Video));
        Assert.Contains("-map 0:v:0? -map 0:a:0", A(new() { Format = "webm" }, Video));
        Assert.Contains("-c:v libvpx-vp9 -crf 32 -b:v 0", A(new() { Format = "webm" }, Video));
        Assert.Contains("-c:a libopus", A(new() { Format = "webm" }, Video));
        Assert.EndsWith("-c:a copy -metadata title=a b out", A(new() { Format = "mkv", Extra = "-metadata title=\"a b\"" }, Video));
    }

    [Fact]
    public void ExtraArgumentsAreSplitAsAShellWould()
    {
        Assert.Equal(new[] { "-metadata", "title=a b", "it's", "x\\y" }, TranscodePlan.SplitArguments("-metadata \"title=a b\"  it\\'s 'x\\y'"));
        Assert.Empty(TranscodePlan.SplitArguments("   "));
        Assert.Equal("ffmpeg -i \"my song.mp3\" out.ogg", TranscodePlan.CommandLine(new[] { "-i", "my song.mp3", "out.ogg" }));
    }

    [Fact]
    public void OutputsNeverOverwriteTheInputOrEachOther()
    {
        var ogg = TranscodeFormat.Find("ogg");
        Assert.Equal(Path.Combine(root, "song.ogg"), TranscodePlan.OutputPath(Path.Combine(root, "song.mp3"), root, ogg));
        Assert.Equal(Path.Combine(root, "song (2).ogg"), TranscodePlan.OutputPath(Path.Combine(root, "song.ogg"), root, ogg));
        var taken = new HashSet<string>();
        TranscodePlan.OutputPath(Path.Combine(root, "a", "song.mp3"), root, ogg, taken);
        Assert.Equal(Path.Combine(root, "song (2).ogg"), TranscodePlan.OutputPath(Path.Combine(root, "b", "song.wav"), root, ogg, taken));
    }

    // With the ffmpeg on this machine (skipped where there is none): a tone probed, converted to
    // every audio format, and read back.
    [Fact]
    public async Task RealFfmpegConvertsToEveryAudioFormat()
    {
        if (FfmpegTools.FindOnSystem() is not { Ffprobe: { } ffprobe } found) return;
        string wav = Path.Combine(root, "tone.wav");
        WriteTone(wav, 44100, 1.5);
        var info = await FfmpegRunner.ProbeAsync(ffprobe, wav);
        Assert.Equal(1.5, info.Duration, 2);
        Assert.Equal("pcm_s16le", info.Audio[0].Codec);
        foreach (var format in TranscodeFormat.All.Where(f => !f.IsVideo))
        {
            string output = Path.Combine(root, $"out-{format.Id}.{format.Extension}");
            var reports = new List<double>();
            var options = new TranscodeOptions { Format = format.Id, FadeIn = 0.2, Normalize = true };
            await FfmpegRunner.ConvertAsync(found.Ffmpeg, TranscodePlan.Arguments(info, options, new Trim(0.5, null), wav, output), output, 1.0, new Progress(reports.Add), CancellationToken.None);
            var result = await FfmpegRunner.ProbeAsync(ffprobe, output);
            Assert.Equal(1.0, result.Duration, 1);
            Assert.Equal(options.EffectiveAudioCodec switch { "libmp3lame" => "mp3", "libvorbis" => "vorbis", "libopus" => "opus", "pcm" => "pcm_s16le", var c => c }, result.Audio[0].Codec);
            Assert.Contains(1.0, reports);
        }
        // Decoded back to samples to hear and draw.
        var samples = await FfmpegRunner.DecodeAsync(found.Ffmpeg, wav, 0, 8000, 1);
        Assert.InRange(samples.Length, 11900, 12100);
        Assert.InRange(samples.Max(), 0.4f, 0.6f);
    }

    [Fact]
    public async Task AFailedOrCancelledConversionLeavesNoFile()
    {
        if (FfmpegTools.FindOnSystem() is not { } found) return;
        string output = Path.Combine(root, "out.ogg");
        await Assert.ThrowsAsync<TranscodeException>(() =>
            FfmpegRunner.ConvertAsync(found.Ffmpeg, new[] { "-y", "-i", Path.Combine(root, "missing.wav"), output }, output, 1, null, CancellationToken.None));
        Assert.False(File.Exists(output));
        string wav = Path.Combine(root, "long.wav");
        WriteTone(wav, 44100, 600);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FfmpegRunner.ConvertAsync(found.Ffmpeg, new[] { "-y", "-i", wav, "-af", "loudnorm", output }, output, 600, null, cancel.Token));
        Assert.False(File.Exists(output));
    }

    // A 440 Hz tone at half volume, 16-bit mono.
    static void WriteTone(string path, int rate, double seconds)
    {
        int frames = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + frames * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 2);
        for (int i = 0; i < frames; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 16383));
    }

    sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
