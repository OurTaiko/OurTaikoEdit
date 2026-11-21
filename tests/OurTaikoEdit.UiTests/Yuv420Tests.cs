using System.Diagnostics;
using OurTaikoEdit.App.Export;
using OurTaikoEdit.Audio;

namespace OurTaikoEdit.UiTests;

// A video's frames as YUV 4:2:0 (BT.709, video range), made by the app for ffmpeg (Yuv420, FfmpegSink).
public class Yuv420Tests
{
    // A frame of one colour, as RGBA or BGRA bytes.
    static byte[] Solid(int width, int height, byte r, byte g, byte b, bool rgba = true)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = rgba ? r : b;
            pixels[i + 1] = g;
            pixels[i + 2] = rgba ? b : r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    static (byte Y, byte U, byte V) First(byte[] yuv, int width, int height) =>
        (yuv[0], yuv[width * height], yuv[width * height + width * height / 4]);

    [Theory]
    // BT.709's own values in the video range.
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 255, 0, 173, 42, 26)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    [InlineData(128, 128, 128, 126, 128, 128)]
    public void AColourIsItsBt709ValuesInTheVideoRange(int r, int g, int b, int y, int u, int v)
    {
        foreach (bool rgba in new[] { true, false })
        {
            var yuv = new byte[Yuv420.Size(8, 4)];
            Yuv420.Convert(Solid(8, 4, (byte)r, (byte)g, (byte)b, rgba), yuv, 8, 4, rgba);
            Assert.Equal(((byte)y, (byte)u, (byte)v), First(yuv, 8, 4));
            // Every sample alike.
            Assert.All(yuv.AsSpan(0, 32).ToArray(), s => Assert.Equal(y, s));
            Assert.All(yuv.AsSpan(32, 8).ToArray(), s => Assert.Equal(u, s));
            Assert.All(yuv.AsSpan(40, 8).ToArray(), s => Assert.Equal(v, s));
        }
    }

    [Fact]
    public void AChromaSampleIsItsBlocksAverageAndEachPixelKeepsItsLuma()
    {
        // 2×2: the left column black, the right one white.
        var pixels = Solid(2, 2, 0, 0, 0);
        for (int row = 0; row < 2; row++)
            for (int c = 0; c < 3; c++) pixels[row * 8 + 4 + c] = 255;
        var yuv = new byte[Yuv420.Size(2, 2)];
        Yuv420.Convert(pixels, yuv, 2, 2, rgba: true);
        Assert.Equal(new byte[] { 16, 235, 16, 235, 128, 128 }, yuv);
    }

    [Fact]
    public void ABigFrameIsConvertedWholeOnEveryCore()
    {
        // Rows far apart in a 1080p frame, each its own colour: each piece of the work did its rows.
        const int width = 1920, height = 1080;
        var pixels = Solid(width, height, 0, 0, 0);
        foreach (int row in new[] { 0, 1, 538, 539, 1078, 1079 })
            for (int x = 0; x < width; x++) pixels[(row * width + x) * 4] = 255;
        var yuv = new byte[Yuv420.Size(width, height)];
        Yuv420.Convert(pixels, yuv, width, height, rgba: true);
        foreach (int row in new[] { 0, 1, 538, 539, 1078, 1079 }) Assert.Equal(63, yuv[row * width + 7]);
        Assert.Equal(16, yuv[2 * width + 7]);
        Assert.Equal(16, yuv[1000 * width + 7]);
    }

    [Fact]
    public void OddSizesAndShortBuffersAreRefused()
    {
        Assert.Throws<ArgumentException>(() => Yuv420.Convert(new byte[3 * 2 * 4], new byte[9], 3, 2, true));
        Assert.Throws<ArgumentException>(() => Yuv420.Convert(new byte[2 * 2 * 4 - 1], new byte[Yuv420.Size(2, 2)], 2, 2, true));
        Assert.Throws<ArgumentException>(() => Yuv420.Convert(new byte[2 * 2 * 4], new byte[Yuv420.Size(2, 2) - 1], 2, 2, true));
    }

    // Through ffmpeg and back: the colours of an encoded video are the ones drawn, which they are
    // only if the file says the matrix and range its YUV is in.
    [Fact]
    public async Task TheEncodedVideoShowsTheColoursDrawn()
    {
        string? ffmpeg = VideoExporter.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");
        string folder = Path.Combine(Path.GetTempPath(), "ourtaikoedit-yuv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const int width = 64, height = 64;
            string audio = Path.Combine(folder, "a.wav");
            File.WriteAllBytes(audio, new PcmAudio { SampleRate = 48000, Channels = 2, Samples = new float[48000 * 2] }.ToWav());
            string output = Path.Combine(folder, "v.mp4");
            var options = new VideoExportOptions(output, width, height, 30, false, false, ffmpeg!, "ultrafast");
            // Quadrants: an orange, a teal, a purple and a light grey.
            byte[][] colours = [[230, 120, 30], [20, 160, 150], [120, 40, 200], [200, 200, 200]];
            var frame = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var c = colours[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
                    int i = (y * width + x) * 4;
                    (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = (c[0], c[1], c[2], 255);
                }
            var sink = new FfmpegSink();
            await sink.StartAsync(options, width, height, rgba: true, audio, CancellationToken.None);
            for (int n = 0; n < 30; n++) Assert.True(await sink.WriteAsync(frame, n, CancellationToken.None));
            await sink.FinishAsync(CancellationToken.None);

            string back = Path.Combine(folder, "back.rgb");
            var info = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-y", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", back })
                info.ArgumentList.Add(arg);
            using (var process = Process.Start(info)!)
            {
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }
            var rgb = File.ReadAllBytes(back);
            Assert.Equal(width * height * 3, rgb.Length);
            // The middle of each quadrant, within what 8-bit YUV and x264 change.
            foreach (var (x, y, c) in new[] { (16, 16, colours[0]), (48, 16, colours[1]), (16, 48, colours[2]), (48, 48, colours[3]) })
                for (int k = 0; k < 3; k++)
                    Assert.InRange(rgb[(y * width + x) * 3 + k], c[k] - 4, c[k] + 4);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
