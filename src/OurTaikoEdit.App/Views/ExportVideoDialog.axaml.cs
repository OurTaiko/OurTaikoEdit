using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OurTaikoEdit.App.Export;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App.Views;

// The preview video's size, frame rate, start, sounds, file and ffmpeg. Starts from the last
// video's choices (kept in the settings by the editor once the video is asked for).
public partial class ExportVideoDialog : Window
{
    static readonly (int Width, int Height, string Name)[] Sizes =
    {
        (3840, 2160, "4K"), (2560, 1440, "1440p"), (1920, 1080, "1080p"), (1600, 900, "900p"), (1280, 720, "720p"), (854, 480, "480p"),
    };
    static readonly int[] Rates = { 24, 25, 30, 50, 60, 120 };

    readonly VideoExportRequest? request;
    readonly AppSettings? settings;

    public ExportVideoDialog() => InitializeComponent();

    public ExportVideoDialog(AppSettings settings, VideoExportRequest request) : this()
    {
        this.settings = settings;
        this.request = request;

        SizeBox.ItemsSource = Sizes.Select(s => $"{s.Width} × {s.Height} ({s.Name})").Append(Loc.T("VideoCustom")).ToList();
        int size = Array.FindIndex(Sizes, s => s.Width == settings.VideoWidth && s.Height == settings.VideoHeight);
        SizeBox.SelectedIndex = size >= 0 ? size : Sizes.Length;
        foreach (var box in new[] { WidthBox, HeightBox })
        {
            box.Minimum = AppSettings.MinVideoSide;
            box.Maximum = AppSettings.MaxVideoSide;
        }
        WidthBox.Value = settings.VideoWidth;
        HeightBox.Value = settings.VideoHeight;
        SizeBox.SelectionChanged += (_, _) => { CustomSize.IsVisible = SizeBox.SelectedIndex == Sizes.Length; ShowHint(); };
        WidthBox.ValueChanged += (_, _) => ShowHint();
        HeightBox.ValueChanged += (_, _) => ShowHint();
        CustomSize.IsVisible = SizeBox.SelectedIndex == Sizes.Length;

        FpsBox.ItemsSource = Rates.Select(r => r.ToString(CultureInfo.InvariantCulture)).Append(Loc.T("VideoCustom")).ToList();
        int rate = Array.IndexOf(Rates, settings.VideoFps);
        FpsBox.SelectedIndex = rate >= 0 ? rate : Rates.Length;
        CustomFps.Minimum = 1;
        CustomFps.Maximum = AppSettings.MaxVideoFps;
        CustomFps.Value = settings.VideoFps;
        FpsBox.SelectionChanged += (_, _) => { CustomFps.IsVisible = FpsBox.SelectedIndex == Rates.Length; ShowHint(); };
        CustomFps.ValueChanged += (_, _) => ShowHint();
        CustomFps.IsVisible = FpsBox.SelectedIndex == Rates.Length;

        RangeBox.ItemsSource = new[] { Loc.T("VideoFromStart"), Loc.T("VideoFromPlayhead") };
        RangeBox.SelectedIndex = 0;
        RangeBox.SelectionChanged += (_, _) => ShowHint();
        EncodingBox.ItemsSource = VideoEncodings.All.Select(EncodingName).ToList();
        EncodingBox.SelectedIndex = Math.Max(0, VideoEncodings.All.ToList().IndexOf(VideoEncodings.Normalize(settings.VideoEncoding)));
        EncodingBox.SelectionChanged += (_, _) => { OutputBox.Text = WithExtension(OutputBox.Text ?? ""); ShowHint(); };
        HitsBox.IsChecked = request.HitSounds;
        OutputBox.Text = WithExtension(request.SuggestedOutput);
        // The one chosen before, else (it is gone) the one found where ffmpeg usually is.
        FfmpegBox.Text = VideoExporter.FindFfmpeg(settings.Ffmpeg) ?? VideoExporter.FindFfmpeg(null) ?? settings.Ffmpeg ?? "";
        ShowHint();
        if (VideoExporter.FindFfmpeg(FfmpegBox.Text) == null) ShowError(Loc.T("VideoNoFfmpeg"));
    }

    bool FromPlayhead => RangeBox.SelectedIndex == 1;
    string Encoding => EncodingBox.SelectedIndex >= 0 ? VideoEncodings.All[EncodingBox.SelectedIndex] : VideoEncodings.Default;
    bool Raw => Encoding == VideoEncodings.None;

    static string EncodingName(string encoding) => encoding switch
    {
        VideoEncodings.None => Loc.T("VideoEncodingNone"),
        VideoEncodings.Default => Loc.F("VideoEncodingDefault", encoding),
        "ultrafast" or "superfast" or "veryfast" => Loc.F("VideoEncodingFast", encoding),
        "slow" or "slower" or "veryslow" => Loc.F("VideoEncodingSlow", encoding),
        _ => encoding,
    };

    // The path with the extension of the encoding chosen (.mp4, or .mov for frames not encoded);
    // another extension is kept, one of the two is swapped.
    string WithExtension(string path)
    {
        string want = VideoEncodings.Extension(Encoding);
        if (path.Length == 0) return path;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
            return path[..^ext.Length] + want;
        return path + want;
    }

    void ShowHint()
    {
        if (request == null) return;
        double start = FromPlayhead ? request.Playhead - PlayViewModel.LeadIn : request.Beginning;
        Hint.Text = Loc.F("VideoHint", start.ToString("0.0", CultureInfo.InvariantCulture));
        if (Raw && settings != null)
        {
            // Three bytes a pixel, every frame: say how big that gets.
            var (width, height, fps) = Chosen();
            double perMinute = width * (double)height * 3 * fps * 60;
            Hint.Text += " " + Loc.F("VideoRawSize", (perMinute / 1e9).ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    void ShowError(string? error)
    {
        Error.Text = error;
        Error.IsVisible = error != null;
    }

    async void BrowseOutput(object? sender, RoutedEventArgs e)
    {
        string current = OutputBox.Text?.Trim() ?? "";
        IStorageFolder? folder = null;
        try
        {
            string? dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) folder = await StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch (ArgumentException) { }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("VideoOutput"),
            SuggestedFileName = Path.GetFileName(current),
            SuggestedStartLocation = folder,
            DefaultExtension = Raw ? "mov" : "mp4",
            FileTypeChoices = new[] { Raw
                ? new FilePickerFileType(Loc.T("VideoRawFileType")) { Patterns = new[] { "*.mov" } }
                : new FilePickerFileType(Loc.T("VideoFileType")) { Patterns = new[] { "*.mp4" } } },
        });
        if (file?.TryGetLocalPath() is { } path) OutputBox.Text = path;
    }

    async void BrowseFfmpeg(object? sender, RoutedEventArgs e)
    {
        var dialogs = new DialogService(this);
        if (await dialogs.PickFileAsync(Loc.T("VideoFfmpeg"), "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") is { } file)
        {
            FfmpegBox.Text = file;
            ShowError(null);
        }
    }

    void Accept(object? sender, RoutedEventArgs e)
    {
        if (settings == null || request == null) { Close(null); return; }
        string output = OutputBox.Text?.Trim() ?? "";
        if (output.Length == 0 || !Path.IsPathRooted(output)) { ShowError(Loc.T("VideoNeedOutput")); return; }
        output = WithExtension(output);
        string? dir = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ShowError(Loc.T("VideoNeedOutput")); return; }
        string typed = FfmpegBox.Text?.Trim() ?? "";
        if (VideoExporter.FindFfmpeg(typed.Length > 0 ? typed : null) is not { } ffmpeg) { ShowError(Loc.T("VideoNoFfmpeg")); return; }
        // The ffmpeg chosen by hand is remembered; one found on its own is looked for again next time.
        settings.Ffmpeg = typed.Length > 0 && typed != VideoExporter.FindFfmpeg(null) ? typed : null;

        var (width, height, fps) = Chosen();
        Close(new VideoExportOptions(output, width, height, fps, FromPlayhead, HitsBox.IsChecked == true, ffmpeg, Encoding));
    }

    (int Width, int Height, int Fps) Chosen()
    {
        int width, height;
        if (SizeBox.SelectedIndex >= 0 && SizeBox.SelectedIndex < Sizes.Length) (width, height, _) = Sizes[SizeBox.SelectedIndex];
        else { width = (int)(WidthBox.Value ?? 1920); height = (int)(HeightBox.Value ?? 1080); }
        width = Math.Clamp(width, AppSettings.MinVideoSide, AppSettings.MaxVideoSide) & ~1;
        height = Math.Clamp(height, AppSettings.MinVideoSide, AppSettings.MaxVideoSide) & ~1;
        int fps = FpsBox.SelectedIndex >= 0 && FpsBox.SelectedIndex < Rates.Length ? Rates[FpsBox.SelectedIndex] : (int)(CustomFps.Value ?? 60);
        return (width, height, Math.Clamp(fps, 1, AppSettings.MaxVideoFps));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
