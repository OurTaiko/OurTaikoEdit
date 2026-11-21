using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.App.Views;
using OurTaikoEdit.Core.Ffmpeg;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using OurTaikoEdit.Core.Transcoding;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.UiTests;

// The transcoder over the project list and the editor, with the ffmpeg on this machine (the tests
// that convert are skipped where there is none), and without any (the download panel).
public class TranscodeSceneTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-transcode-ui-" + Guid.NewGuid().ToString("N"));
    static string Shots => Environment.GetEnvironmentVariable("OURTAIKOEDIT_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");

    public TranscodeSceneTests()
    {
        ChartLanguages.Register(new TjaLanguage());
        EditorViewModel.OpenPlayer = _ => null;
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    // A main window whose ffmpeg is the one given (a path that is not there: none at all).
    (MainWindow Window, MainWindowViewModel Vm, ProjectRegistry Registry) Open(string ffmpeg)
    {
        var registry = new ProjectRegistry(Path.Combine(root, "data"));
        var window = new MainWindow { Width = 1440, Height = 900 };
        var vm = new MainWindowViewModel(registry, new DialogService(window), new AppSettings { Language = "en", Ffmpeg = ffmpeg });
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, registry);
    }

    static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Shots, name + ".png"));
    }

    static void Until(Func<bool> done, int ms = 30000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > ms) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(Window window, string? name = null) where T : Control
        => window.GetVisualDescendants().OfType<T>().First(c => name == null || c.Name == name);

    [AvaloniaFact]
    public void WithoutFfmpegTheTranscoderOffersToDownloadOne()
    {
        var (window, vm, _) = Open(Path.Combine(root, "no", "ffmpeg"));
        Find<Button>(window, "TranscodeButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var transcode = Assert.IsType<TranscodeViewModel>(vm.Transcode);
        Assert.True(transcode.NeedsFfmpeg);
        Assert.True(Find<Button>(window, "DownloadButton").IsEffectivelyVisible == (FfmpegTools.CurrentRid != null));
        Shot(window, "transcode-no-ffmpeg");
        transcode.BackCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Transcode);
        Assert.IsType<ProjectsViewModel>(vm.Scene);
    }

    // Where there is no package (Linux, Intel Macs): no download button, only choosing one.
    [AvaloniaFact]
    public void WithoutAPackageForThePlatformThereIsNothingToDownload()
    {
        var (window, vm, _) = Open(Path.Combine(root, "no", "ffmpeg"));
        var tools = new FfmpegTools(Path.Combine(root, "data"), manifest: new Uri(Path.Combine(root, "none.json")), rid: null);
        vm.Transcode = new TranscodeViewModel(tools, new DialogService(window), vm.Settings, () => vm.Transcode = null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Transcode.NeedsFfmpeg);
        Assert.False(Find<Button>(window, "DownloadButton").IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == vm.Transcode.NoDownloadText && t.Text.Length > 0);
    }

    [AvaloniaFact]
    public void FilesAreReadTrimmedAndConverted()
    {
        if (FfmpegTools.FindOnSystem() is not { Ffprobe: not null } found) return;
        string song = Path.Combine(root, "song.wav"), video = Path.Combine(root, "clip.mp4");
        Make(found.Ffmpeg, "-f lavfi -i sine=frequency=330:sample_rate=44100:duration=6 -af volume=0.6 " + Quote(song));
        Make(found.Ffmpeg, "-f lavfi -i testsrc2=size=320x240:rate=30:duration=2 -f lavfi -i sine=frequency=550:duration=2 -c:v libx264 -c:a aac -shortest " + Quote(video));
        var (window, vm, _) = Open(found.Ffmpeg);
        vm.OpenTranscode(new[] { song, video }, "ogg");
        var t = vm.Transcode!;
        Until(() => t.Items.All(i => i.Status == TranscodeStatus.Ready) && t.Waveform != null);
        Assert.Equal(6, t.Items[0].Duration, 1);
        Assert.Contains("H264 320×240", t.Items[1].Description);
        Assert.Contains("libvorbis", t.CommandLine);

        // The first file cut to 1–4 s; the format's choices follow the format.
        t.Items[0].TrimStart = 1;
        t.Items[0].TrimEnd = 4;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("-ss 1 ", t.CommandLine);
        Assert.Contains("-t 3 ", t.CommandLine);
        Assert.True(t.ShowQuality && !t.ShowBitrate);
        t.Format = t.Formats.First(f => f.Value == "mp3");
        Assert.Contains("libmp3lame", t.CommandLine);
        t.Format = t.Formats.First(f => f.Value == "ogg");
        t.ShowAdvanced = true;
        Shot(window, "transcode");

        t.StartCommand.Execute(null);
        Until(() => !t.IsRunning && t.Items.All(i => i.IsDone));
        string songOut = Path.Combine(root, "song.ogg"), clipOut = Path.Combine(root, "clip.ogg");
        Assert.Equal(songOut, t.Items[0].Output);
        Assert.Equal(clipOut, t.Items[1].Output);
        var info = Probe(found.Ffprobe!, songOut);
        Assert.Equal(3, info.Duration, 1);
        Assert.Equal("vorbis", info.Audio[0].Codec);
        Assert.Null(Probe(found.Ffprobe!, clipOut).Video);

        // To a video format: the picture is copied when nothing changes it.
        t.Format = t.Formats.First(f => f.Value == "mkv");
        t.Selected = t.Items[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("-c:v copy", t.CommandLine);
        Shot(window, "transcode-done");
    }

    [AvaloniaFact]
    public void TheEditorsSongIsConvertedAndUsed()
    {
        if (FfmpegTools.FindOnSystem() is not { Ffprobe: not null } found) return;
        var (window, vm, registry) = Open(found.Ffmpeg);
        string folder = Path.Combine(root, "Sample");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sample.tja"), Path.Combine(folder, "Sample.tja"));
        Make(found.Ffmpeg, "-f lavfi -i sine=frequency=330:sample_rate=44100:duration=3 " + Quote(Path.Combine(folder, "song.wav")));
        var project = registry.OpenFolder(folder);
        var editor = new EditorViewModel(project, new DialogService(window), () => { }, vm.Settings, scene => vm.Play = scene, vm.Tools, (f, format, done) => vm.OpenTranscode(f, format, done));
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        // The song is the WAV, named by the chart.
        editor.UseSong(Path.Combine(folder, "song.wav"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("WAVE:song.wav", Find<TextEditor>(window).Text);
        Until(() => editor.AudioStatus.Contains("song.wav"));

        Find<Button>(window, "ConvertSongButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var t = vm.Transcode!;
        Until(() => t.Items.Count == 1 && t.Items[0].Status == TranscodeStatus.Ready);
        Assert.Equal("ogg", t.Format.Value);
        t.StartCommand.Execute(null);
        Until(() => !t.IsRunning);
        Assert.True(t.Items[0].IsDone, t.Items[0].Error);
        Assert.Contains("WAVE:song.ogg", Find<TextEditor>(window).Text);
        t.BackCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(editor, vm.Scene);
        Assert.True(editor.IsDirty);
    }

    // Off the UI thread: blocking it on a task that comes back to it would never end.
    static MediaInfo Probe(string ffprobe, string file) => Task.Run(() => FfmpegRunner.ProbeAsync(ffprobe, file)).GetAwaiter().GetResult();

    static string Quote(string path) => "\"" + path + "\"";

    static void Make(string ffmpeg, string args)
    {
        var info = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in TranscodePlan.SplitArguments("-hide_banner -v error -y " + args)) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }
}
