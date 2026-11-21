using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using OurTaiko;
using OurTaikoEdit.App.Export;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.UiTests;

// The preview video: the play scene with auto play on the video's own clock, its sounds mixed.
public class VideoExportTests
{
    // 120 BPM, measures of 2 s from 0: a don at 0, a ka at 0.25, a big don at 0.5, a drumroll from
    // 1 to 2, then a don at 2.5.
    static TaikoChart Chart() => new TjaLanguage().Analyze("TITLE:Video\nBPM:120\nOFFSET:0\nCOURSE:Oni\nLEVEL:8\n#START\n1020300050000000,\n8000100000000000,\n#END\n", 0).Chart!;

    static VideoExportSource Source(PcmAudio? song) => new(Chart(), "Video", "", 0, BranchRoute.Normal, song,
        HitSounds.Don(48000), HitSounds.Ka(48000), 1, 1);

    static PcmAudio Silence(double seconds, int rate = 44100) => new() { SampleRate = rate, Channels = 2, Samples = new float[(int)(seconds * rate) * 2] };

    static VideoExportOptions Options(string output = "", int fps = 30) => new(output, 640, 360, fps, false, true, "");

    [Fact]
    public void AutoPlaysHitsAreHeardAtTheirNotesWhateverTheFrameRate()
    {
        foreach (int fps in new[] { 24, 60 })
        {
            var (hits, frames, begin) = VideoExporter.Rehearse(Source(Silence(6)), Options(fps: fps));
            // The play starts two seconds before the first note and ends two seconds after the last.
            Assert.Equal(-2, begin, 6);
            int expected = (int)Math.Floor((Chart().Duration + 2 - begin) * fps) + 1;
            Assert.InRange(frames, expected - 1, expected + 1);
            // The notes' sounds at their very times, not at the frames that judged them.
            foreach (double at in new[] { 0, 0.25, 0.5, 2.5 })
                Assert.Contains(hits, h => Math.Abs(h.At - at) < 1e-9);
            Assert.Contains(hits, h => Math.Abs(h.At - 0.25) < 1e-9 && h.Ka);
            Assert.Contains(hits, h => Math.Abs(h.At - 0.5) < 1e-9 && h.Big);
            // The drumroll is hit through its length.
            Assert.True(hits.Count(h => h.At >= 1 && h.At < 2) >= 10);
        }
    }

    [Fact]
    public void ABalloonsPopIsHeardOnceWithTheHitThatPopsItAndAKusudamasBreakingToo()
    {
        // A balloon of 5 from 0 to 1, a kusudama of 4 from 2 to 3.
        var chart = new TjaLanguage().Analyze("TITLE:Pop\nBPM:120\nOFFSET:0\nBALLOON:5,4\nCOURSE:Oni\nLEVEL:8\n#START\n7000000080000000,\n9000000080000000,\n#END\n", 0).Chart!;
        var source = new VideoExportSource(chart, "Pop", "", 0, BranchRoute.Normal, Silence(6), HitSounds.Don(48000), HitSounds.Ka(48000), 1, 1);
        var (hits, _, _) = VideoExporter.Rehearse(source, Options());
        var balloon = hits.Where(h => h.Pop == PopKind.None && h.At >= 0 && h.At < 1).OrderBy(h => h.At).ToList();
        Assert.Equal(5, balloon.Count);
        var pop = Assert.Single(hits, h => h.Pop == PopKind.Balloon);
        Assert.Equal(balloon[^1].At, pop.At, 9);
        var ball = hits.Where(h => h.Pop == PopKind.None && h.At >= 2 && h.At < 3).OrderBy(h => h.At).ToList();
        Assert.Equal(4, ball.Count);
        var broken = Assert.Single(hits, h => h.Pop == PopKind.Kusudama);
        // Heard when the breaking hit is judged, within a frame of it.
        Assert.InRange(broken.At, ball[^1].At - 1e-9, ball[^1].At + 1.0 / 30 + 1e-9);

        // Without hit sounds there are no pops either.
        var (quiet, _, _) = VideoExporter.Rehearse(source, Options() with { HitSounds = false });
        Assert.DoesNotContain(quiet, h => h.Pop != PopKind.None);
    }

    [Fact]
    public void ThePopsAreMixedWithTheirOwnSoundsAndTheGamesAreShipped()
    {
        var (balloon, kusudama) = PopSounds.Load();
        Assert.NotNull(balloon);
        Assert.NotNull(kusudama);
        // A pop of a single loud sample, so its place in the mix shows.
        var mark = new PcmAudio { SampleRate = 48000, Channels = 1, Samples = [0.9f] };
        var source = Source(Silence(2, 48000)) with { BalloonPop = mark, KusudamaPop = null };
        var mix = VideoExporter.Mix(source, new[] { new RecordedHit(0.5, false, true, PopKind.Balloon), new RecordedHit(1, false, true, PopKind.Kusudama) },
            begin: 0, seconds: 2);
        int at = 48000 / 2;
        Assert.Equal(0.9f, mix.Samples[at * 2], 3);
        Assert.Equal(0.9f, mix.Samples[at * 2 + 1], 3);
        // Nothing else: the pop is not a don, and a missing kusudama sound is silence.
        Assert.Equal(2, mix.Samples.Count(s => s != 0));
    }

    [Fact]
    public void TheSoundIsTheSongFromTheVideosStartWithTheHitsOverIt()
    {
        var song = Silence(6, 48000);
        // A mark in the song at 1 s.
        song.Samples[48000 * 2] = 0.5f;
        var source = Source(song);
        var mix = VideoExporter.Mix(source, new[] { new RecordedHit(0.5, false, true) }, begin: -2, seconds: 4);
        Assert.Equal(48000, mix.SampleRate);
        Assert.Equal(2, mix.Channels);
        Assert.Equal(4 * 48000, mix.Frames);
        // The video starts 2 s before the song: the song's 1 s is the video's 3 s.
        Assert.Equal(0.5f, mix.Samples[3 * 48000 * 2]);
        // Nothing before the hit at the song's 0.5 s (the video's 2.5 s), the don from there.
        int hit = (int)(2.5 * 48000);
        Assert.All(mix.Samples.Take(hit * 2), s => Assert.True(s == 0));
        Assert.Contains(mix.Samples.Skip(hit * 2).Take(4800), s => Math.Abs(s) > 0.1f);
    }

    [AvaloniaFact]
    public async Task TheVideoIsEncodedAtTheChosenSizeAndRateWithItsSound()
    {
        string? ffmpeg = VideoExporter.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");
        string folder = Path.Combine(Path.GetTempPath(), "ourtaikoedit-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "preview.mp4");
            var options = Options(output) with { Ffmpeg = ffmpeg! };
            var reports = new List<(int, int)>();
            await VideoExporter.ExportAsync(Source(Silence(6)), options, new SyncProgress(reports), CancellationToken.None);
            Assert.True(File.Exists(output));
            Assert.Equal(reports[^1].Item2, reports[^1].Item1);

            string probe = Path.Combine(Path.GetDirectoryName(ffmpeg!)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            Assert.SkipWhen(!File.Exists(probe), "ffprobe is not installed");
            string said = Run(probe, "-v", "error", "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration", "-of", "default=noprint_wrappers=1", output);
            Assert.Contains("width=640", said);
            Assert.Contains("height=360", said);
            Assert.Contains("avg_frame_rate=30/1", said);
            Assert.Contains("codec_type=audio", said);
            double duration = double.Parse(said.Split('\n').First(l => l.StartsWith("duration=")).Split('=')[1], CultureInfo.InvariantCulture);
            // From 2 s before the first note to 2 s after the chart's end.
            double length = Chart().Duration + 4;
            Assert.InRange(duration, length - 0.1, length + 0.15);

            // A frame of the play for the eye: the drumroll under the judge.
            string shots = Environment.GetEnvironmentVariable("OURTAIKOEDIT_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(shots);
            Run(ffmpeg!, "-v", "error", "-y", "-ss", "3.5", "-i", output, "-frames:v", "1", Path.Combine(shots, "video-frame.png"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task EachEncodingIsWrittenAndNoneKeepsTheFramesAsTheyAre()
    {
        string? ffmpeg = VideoExporter.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");
        string probe = Path.Combine(Path.GetDirectoryName(ffmpeg!)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        Assert.SkipWhen(!File.Exists(probe), "ffprobe is not installed");
        string folder = Path.Combine(Path.GetTempPath(), "ourtaikoedit-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (encoding, video, audio) in new[] { ("ultrafast", "h264", "aac"), ("veryslow", "h264", "aac"), (VideoEncodings.None, "rawvideo", "pcm_s16le") })
            {
                string output = Path.Combine(folder, encoding + VideoEncodings.Extension(encoding));
                var options = Options(output, fps: 24) with { Ffmpeg = ffmpeg!, Width = 320, Height = 180, Encoding = encoding };
                await VideoExporter.ExportAsync(Source(Silence(6)), options, null, CancellationToken.None);
                string said = Run(probe, "-v", "error", "-show_entries", "stream=codec_name,pix_fmt,nb_frames", "-of", "default=noprint_wrappers=1", output);
                Assert.Contains("codec_name=" + video, said);
                Assert.Contains("codec_name=" + audio, said);
                if (encoding == VideoEncodings.None)
                {
                    Assert.EndsWith(".mov", output);
                    Assert.Contains("pix_fmt=rgb24", said);
                    // Every frame whole: three bytes a pixel.
                    int frames = int.Parse(said.Split('\n').First(l => l.StartsWith("nb_frames=")).Split('=')[1], CultureInfo.InvariantCulture);
                    Assert.True(new FileInfo(output).Length >= (long)frames * 320 * 180 * 3);
                }
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task ACancelledVideoLeavesNoFile()
    {
        string? ffmpeg = VideoExporter.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");
        string output = Path.Combine(Path.GetTempPath(), "ourtaikoedit-cancelled-" + Guid.NewGuid().ToString("N") + ".mp4");
        using var stop = new CancellationTokenSource();
        var progress = new SyncProgress(new(), p => { if (p.Item1 == 10) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoExporter.ExportAsync(Source(Silence(6)), Options(output) with { Ffmpeg = ffmpeg! }, progress, stop.Token));
        Assert.False(File.Exists(output));
    }

    [AvaloniaFact]
    public void TheDialogStartsFromTheLastVideosChoices()
    {
        var settings = new OurTaikoEdit.Core.Settings.AppSettings { VideoWidth = 1280, VideoHeight = 720, VideoFps = 48, Ffmpeg = "/nowhere/ffmpeg" };
        var dialog = new OurTaikoEdit.App.Views.ExportVideoDialog(settings, new OurTaikoEdit.App.ViewModels.VideoExportRequest("/tmp/Song (Oni).mp4", 30, -2, true));
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var size = dialog.FindControl<Avalonia.Controls.ComboBox>("SizeBox")!;
        var fps = dialog.FindControl<Avalonia.Controls.ComboBox>("FpsBox")!;
        var custom = dialog.FindControl<Avalonia.Controls.NumericUpDown>("CustomFps")!;
        Assert.Equal("1280 × 720 (720p)", size.SelectedItem);
        // 48 is not a preset: the custom rate, with its box shown.
        Assert.True(custom.IsVisible);
        Assert.Equal(48, custom.Value);
        Assert.Equal("/tmp/Song (Oni).mp4", dialog.FindControl<Avalonia.Controls.TextBox>("OutputBox")!.Text);
        // The encoding starts at the default.
        var encoding = dialog.FindControl<Avalonia.Controls.ComboBox>("EncodingBox")!;
        Assert.Equal(VideoEncodings.Default, VideoEncodings.All[encoding.SelectedIndex]);
        // An ffmpeg chosen before that is gone gives way to one found; none at all is said at once.
        Assert.True(dialog.FindControl<Avalonia.Controls.TextBlock>("Error")!.IsVisible == (VideoExporter.FindFfmpeg(null) == null));
        string shots = Environment.GetEnvironmentVariable("OURTAIKOEDIT_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(shots);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(shots, "video-dialog.png"));
        dialog.Close();
    }

    [AvaloniaFact]
    public void NotEncodingMakesAMovAndSaysHowLargeItGets()
    {
        var settings = new OurTaikoEdit.Core.Settings.AppSettings { VideoWidth = 1920, VideoHeight = 1080, VideoFps = 60, VideoEncoding = "veryfast" };
        var dialog = new OurTaikoEdit.App.Views.ExportVideoDialog(settings, new OurTaikoEdit.App.ViewModels.VideoExportRequest("/tmp/Song (Oni).mp4", 30, -2, true));
        dialog.Show();
        var encoding = dialog.FindControl<Avalonia.Controls.ComboBox>("EncodingBox")!;
        var output = dialog.FindControl<Avalonia.Controls.TextBox>("OutputBox")!;
        var hint = dialog.FindControl<Avalonia.Controls.TextBlock>("Hint")!;
        Assert.Equal("veryfast", VideoEncodings.All[encoding.SelectedIndex]);
        Assert.DoesNotContain("GB", hint.Text);
        encoding.SelectedIndex = VideoEncodings.All.ToList().IndexOf(VideoEncodings.None);
        Assert.Equal("/tmp/Song (Oni).mov", output.Text);
        // 1920 × 1080 × 3 bytes × 60 frames × 60 s.
        Assert.Contains(OurTaikoEdit.Core.Localization.Loc.F("VideoRawSize", "22.4"), hint.Text);
        encoding.SelectedIndex = VideoEncodings.All.ToList().IndexOf("slow");
        Assert.Equal("/tmp/Song (Oni).mp4", output.Text);
        dialog.Close();
    }

    // The editor's button: the dialog's choices, the export under the real progress window, the
    // status line after it.
    [AvaloniaFact]
    public async Task TheEditorExportsTheChartBeingEdited()
    {
        string? ffmpeg = VideoExporter.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");
        OurTaikoEdit.Core.Languages.ChartLanguages.Register(new TjaLanguage());
        string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-video-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Sample");
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sample.tja"), Path.Combine(folder, "Sample.tja"));
            var registry = new OurTaikoEdit.Core.Projects.ProjectRegistry(Path.Combine(root, "data"));
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();
            string output = Path.Combine(root, "sample.mp4");
            var dialogs = new Choices(new OurTaikoEdit.App.Views.DialogService(owner), new VideoExportOptions(output, 320, 180, 24, true, true, ffmpeg!, "fast"));
            var settings = new OurTaikoEdit.Core.Settings.AppSettings();
            var editor = new OurTaikoEdit.App.ViewModels.EditorViewModel(registry.OpenFolder(folder), dialogs, () => { }, settings);
            editor.Seek(10);
            await editor.ExportVideoCommand.ExecuteAsync(null);
            Assert.True(File.Exists(output), editor.Message);
            Assert.Equal(OurTaikoEdit.Core.Localization.Loc.F("VideoDone", output), editor.Message);
            // Asked from the playhead, with hit sounds as the editor has them, named after the chart.
            Assert.Equal(10, dialogs.Asked!.Playhead, 6);
            Assert.EndsWith(".mp4", dialogs.Asked.SuggestedOutput);
            // The choices are kept for the next video.
            Assert.Equal((320, 180, 24, "fast"), (settings.VideoWidth, settings.VideoHeight, settings.VideoFps, settings.VideoEncoding));
            owner.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class Choices(OurTaikoEdit.App.Views.DialogService real, VideoExportOptions options) : OurTaikoEdit.App.ViewModels.IDialogService
    {
        public OurTaikoEdit.App.ViewModels.VideoExportRequest? Asked;
        public Task<OurTaikoEdit.App.Export.VideoExportOptions?> ExportVideoAsync(OurTaikoEdit.Core.Settings.AppSettings settings, OurTaikoEdit.App.ViewModels.VideoExportRequest request)
        {
            Asked = request;
            return Task.FromResult<VideoExportOptions?>(options);
        }
        public Task<bool> ProgressAsync(string title, string waiting, Func<int, int, string> describe, Func<IProgress<(int Done, int Total)>, CancellationToken, Task> work) => real.ProgressAsync(title, waiting, describe, work);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<OurTaikoEdit.App.ViewModels.NewProjectRequest?> NewProjectAsync(string location) => Task.FromResult<OurTaikoEdit.App.ViewModels.NewProjectRequest?>(null);
        public Task MessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string confirm) => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string message, string value) => Task.FromResult<string?>(null);
        public Task<string?> BranchConditionAsync(string title, OurTaikoEdit.Core.Languages.IChartLanguage language, string written) => Task.FromResult<string?>(null);
        public Task<OurTaikoEdit.Core.Languages.NewBlock?> NewBlockAsync(OurTaikoEdit.Core.Languages.IChartLanguage language, IReadOnlyList<OurTaikoEdit.Core.Languages.BlockCourse> courses, IReadOnlyList<OurTaikoEdit.Core.Languages.ChartBlock> blocks, int current) => Task.FromResult<OurTaikoEdit.Core.Languages.NewBlock?>(null);
        public Task<OurTaikoEdit.App.ViewModels.SaveChoice> AskSaveAsync(string name) => Task.FromResult(OurTaikoEdit.App.ViewModels.SaveChoice.Discard);
        public Task<bool> SettingsAsync(OurTaikoEdit.Core.Settings.AppSettings settings) => Task.FromResult(false);
    }

    sealed class SyncProgress(List<(int, int)> reports, Action<(int, int)>? then = null) : IProgress<(int Frame, int Frames)>
    {
        public void Report((int Frame, int Frames) value) { reports.Add(value); then?.Invoke(value); }
    }

    static string Run(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return output;
    }
}
