using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Core.Ffmpeg;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Settings;
using OurTaikoEdit.Core.Transcoding;

namespace OurTaikoEdit.App.ViewModels;

// A choice in a list: its value and what the list shows.
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public enum TranscodeStatus { Reading, Ready, Unreadable, Waiting, Running, Done, Failed, Cancelled }

// A file to convert: what ffprobe found in it, the part of it to convert, and how it went.
public sealed partial class TranscodeItem : ObservableObject
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty] MediaInfo? info;
    [ObservableProperty] string description = Loc.T("TranscodeReading");
    [ObservableProperty] TranscodeStatus status = TranscodeStatus.Reading;
    [ObservableProperty] double progress;
    [ObservableProperty] string? error;
    [ObservableProperty] string? output;
    // The part converted, in seconds: from TrimStart to TrimEnd (null: the end).
    [ObservableProperty] double trimStart;
    [ObservableProperty] double? trimEnd;

    public double Duration => Info is { Duration: var d } && double.IsFinite(d) ? d : 0;
    public Trim Trim => new(TrimStart, TrimEnd is { } e && e < Duration - 0.0005 ? e : null);
    public bool IsTrimmed => !Trim.IsWhole;

    public bool IsRunning => Status == TranscodeStatus.Running;
    public bool IsDone => Status == TranscodeStatus.Done;
    public bool HasError => Status is TranscodeStatus.Failed or TranscodeStatus.Unreadable;
    public string StatusText => Status switch
    {
        TranscodeStatus.Reading => Loc.T("TranscodeReading"),
        TranscodeStatus.Unreadable or TranscodeStatus.Failed => Error ?? "",
        TranscodeStatus.Waiting => Loc.T("TranscodeWaiting"),
        TranscodeStatus.Running => Loc.F("TranscodeRunning", (int)(Progress * 100)),
        TranscodeStatus.Done => Loc.F("TranscodeDoneAs", System.IO.Path.GetFileName(Output)),
        TranscodeStatus.Cancelled => Loc.T("TranscodeCancelledOne"),
        _ => IsTrimmed ? Loc.F("TranscodeTrimmed", Clock(Trim.Start), Clock(Trim.End ?? Duration)) : "",
    };

    partial void OnInfoChanged(MediaInfo? value) => OnPropertyChanged(nameof(Duration));
    partial void OnStatusChanged(TranscodeStatus value) => Changed();
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StatusText));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(StatusText));
    partial void OnTrimStartChanged(double value) => TrimChanged();
    partial void OnTrimEndChanged(double? value) => TrimChanged();

    void TrimChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsTrimmed));
    }

    void Changed()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(HasError));
    }

    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds)) return "?";
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}

// The transcoder: audio and video files converted with ffmpeg (after ~/Repos/transcoder): a
// list of files, one set of choices for all of them, the selected file heard and cut on its
// waveform, and the ffmpeg the app downloads (or the one chosen, or the system's).
public sealed partial class TranscodeViewModel : ViewModelBase
{
    readonly FfmpegTools tools;
    readonly IDialogService dialogs;
    readonly AppSettings settings;
    readonly Action back;
    // Told of each file converted (the editor: a song to use).
    readonly Action<string>? converted;
    bool building;
    CancellationTokenSource? run;
    CancellationTokenSource? download;

    public ObservableCollection<TranscodeItem> Items { get; } = new();
    [ObservableProperty] TranscodeItem? selected;
    [ObservableProperty] bool isEmpty = true;

    // ffmpeg
    [ObservableProperty] FfmpegLocation? ffmpeg;
    [ObservableProperty] string ffmpegText = "";
    [ObservableProperty] bool isDownloading;
    [ObservableProperty] double downloadProgress;
    [ObservableProperty] string downloadText = "";
    [ObservableProperty] string? downloadError;
    [ObservableProperty] ToolsManifest? offer;
    public bool HasFfmpeg => Ffmpeg != null;
    public bool NeedsFfmpeg => Ffmpeg == null;
    public bool CanDownload => tools.Rid != null && !IsDownloading;
    public bool HasDownloaded => tools.Installed != null;
    public bool HasOffer => Offer != null && HasFfmpeg && !IsDownloading;
    public string OfferText => Offer is { } o ? Loc.F("FfmpegUpdate", o.Version, Size(o.For(tools.Rid)?.Size ?? 0)) : "";
    public string DownloadButtonText => Offer?.For(tools.Rid) is { } package ? Loc.F("FfmpegDownload", Size(package.Size)) : Loc.T("FfmpegDownloadShort");
    public string NoDownloadText => tools.Rid == null ? Loc.T("FfmpegNoPackage") : "";

    // The choices
    public IReadOnlyList<Choice<string>> Formats { get; } = TranscodeFormat.All
        .Select(f => new Choice<string>(f.Id, f.IsVideo ? Loc.F("TranscodeVideoFormat", f.Label) : f.Label)).ToList();
    [ObservableProperty] Choice<string> format;
    [ObservableProperty] IReadOnlyList<Choice<int>> audioTracks = Array.Empty<Choice<int>>();
    [ObservableProperty] Choice<int>? audioTrack;
    [ObservableProperty] IReadOnlyList<Choice<string>> audioCodecs = Array.Empty<Choice<string>>();
    [ObservableProperty] Choice<string>? audioCodec;
    public IReadOnlyList<Choice<bool>> RateModes { get; } = new[] { new Choice<bool>(true, Loc.T("TranscodeVbr")), new Choice<bool>(false, Loc.T("TranscodeCbr")) };
    [ObservableProperty] Choice<bool>? rateMode;
    [ObservableProperty] IReadOnlyList<Choice<int>> bitrates = Array.Empty<Choice<int>>();
    [ObservableProperty] Choice<int>? bitrate;
    [ObservableProperty] IReadOnlyList<Choice<int>> qualities = Array.Empty<Choice<int>>();
    [ObservableProperty] Choice<int>? quality;
    public IReadOnlyList<Choice<int>> FlacLevels { get; } = Enumerable.Range(0, 9)
        .Select(n => new Choice<int>(n, n switch { 0 => Loc.F("FlacFastest", n), 5 => Loc.F("ChoiceDefault", n), 8 => Loc.F("FlacSmallest", n), _ => n.ToString(CultureInfo.InvariantCulture) })).ToList();
    [ObservableProperty] Choice<int> flacLevel;
    public IReadOnlyList<Choice<string>> PcmDepths { get; } = new[]
    {
        new Choice<string>("pcm_s16le", "16 bit"), new Choice<string>("pcm_s24le", "24 bit"), new Choice<string>("pcm_f32le", Loc.T("PcmFloat")),
    };
    [ObservableProperty] Choice<string> pcmDepth;
    public IReadOnlyList<Choice<int?>> SampleRates { get; } = Keep<int>(Codecs.SampleRates.Select(r => (r, $"{r / 1000.0:0.###} kHz")));
    [ObservableProperty] Choice<int?> sampleRate;
    public IReadOnlyList<Choice<int?>> ChannelChoices { get; } = Keep<int>(new[] { (1, Loc.T("Mono")), (2, Loc.T("Stereo")) });
    [ObservableProperty] Choice<int?> channels;
    [ObservableProperty] decimal volumeDb;
    [ObservableProperty] bool normalize;
    [ObservableProperty] decimal fadeIn;
    [ObservableProperty] decimal fadeOut;
    [ObservableProperty] bool keepMetadata = true;
    [ObservableProperty] IReadOnlyList<Choice<string>> videoCodecs = Array.Empty<Choice<string>>();
    [ObservableProperty] Choice<string>? videoCodec;
    [ObservableProperty] decimal crf = 23;
    [ObservableProperty] decimal crfMax = 51;
    public IReadOnlyList<Choice<string>> Presets { get; } = Codecs.Presets.Select(p => new Choice<string>(p, p == "medium" ? Loc.F("ChoiceDefault", p) : p)).ToList();
    [ObservableProperty] Choice<string> preset;
    public IReadOnlyList<Choice<int?>> Heights { get; } = Keep<int>(Codecs.Heights.Select(h => (h, $"{h}p")));
    [ObservableProperty] Choice<int?> height;
    public IReadOnlyList<Choice<int?>> FrameRates { get; } = Keep<int>(Codecs.FrameRates.Select(f => (f, $"{f} fps")));
    [ObservableProperty] Choice<int?> fps;
    [ObservableProperty] string extra = "";
    [ObservableProperty] bool showAdvanced;
    [ObservableProperty] string outputFolder = "";

    // What the choices show
    [ObservableProperty] bool isVideo;
    [ObservableProperty] bool showAudioEncoding;
    [ObservableProperty] bool showRateMode;
    [ObservableProperty] bool showBitrate;
    [ObservableProperty] bool showQuality;
    [ObservableProperty] bool showFlac;
    [ObservableProperty] bool showDepth;
    [ObservableProperty] bool showSampleRate;
    [ObservableProperty] bool showVideoEncoding;
    [ObservableProperty] bool showPreset;
    [ObservableProperty] string plan = "";
    [ObservableProperty] string commandLine = "";

    // Running
    [ObservableProperty] bool isRunning;
    [ObservableProperty] string runText = "";

    // Hearing the selected file
    [ObservableProperty] Waveform? waveform;
    [ObservableProperty] bool previewLoading;
    [ObservableProperty] string previewText = "";
    [ObservableProperty] bool isPreviewPlaying;
    [ObservableProperty] double previewPosition;
    IAudioPlayer? player;
    PcmAudio? previewAudio;
    int previewLoad;
    readonly DispatcherTimer previewTimer;
    public bool CanPreview => Waveform != null;

    public TranscodeViewModel(FfmpegTools tools, IDialogService dialogs, AppSettings settings, Action back,
        IEnumerable<string>? files = null, string? format = null, Action<string>? converted = null)
    {
        this.tools = tools;
        this.dialogs = dialogs;
        this.settings = settings;
        this.back = back;
        this.converted = converted;
        this.format = Formats.FirstOrDefault(f => f.Value == (format ?? settings.TranscodeFormat)) ?? Formats.First(f => f.Value == "ogg");
        flacLevel = FlacLevels[5];
        pcmDepth = PcmDepths[0];
        sampleRate = SampleRates[0];
        channels = ChannelChoices[0];
        preset = Presets.First(p => p.Value == "medium");
        height = Heights[0];
        fps = FrameRates[0];
        outputFolder = settings.TranscodeFolder ?? "";
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        previewTimer.Tick += (_, _) => PreviewTick();
        Items.CollectionChanged += (_, _) => { IsEmpty = Items.Count == 0; StartCommand.NotifyCanExecuteChanged(); };
        RefreshFfmpeg();
        RebuildCodecs(keep: false);
        if (files != null) Add(files);
        _ = CheckOfferAsync();
    }

    static IReadOnlyList<Choice<int?>> Keep<T>(IEnumerable<(int Value, string Label)> choices)
        => new[] { new Choice<int?>(null, Loc.T("KeepAsIs")) }.Concat(choices.Select(c => new Choice<int?>(c.Value, c.Label))).ToList();

    static string Size(long bytes) => bytes <= 0 ? "?" : $"{bytes / 1048576.0:0.#} MB";

    // ── ffmpeg ──────────────────────────────────────────────────────────────

    void RefreshFfmpeg()
    {
        Ffmpeg = tools.Find(settings.Ffmpeg);
        FfmpegText = Ffmpeg switch
        {
            null => Loc.T("FfmpegMissing"),
            { Origin: FfmpegOrigin.Downloaded } => Loc.F("FfmpegDownloaded", tools.Installed?.Version),
            { Origin: FfmpegOrigin.Chosen } f => Loc.F("FfmpegChosen", f.Ffmpeg),
            var f => Loc.F("FfmpegSystem", f.Ffmpeg),
        };
        OnPropertyChanged(nameof(HasFfmpeg));
        OnPropertyChanged(nameof(NeedsFfmpeg));
        OnPropertyChanged(nameof(HasDownloaded));
        OnPropertyChanged(nameof(HasOffer));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnOfferChanged(ToolsManifest? value)
    {
        OnPropertyChanged(nameof(HasOffer));
        OnPropertyChanged(nameof(OfferText));
        OnPropertyChanged(nameof(DownloadButtonText));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(HasOffer));
        DownloadFfmpegCommand.NotifyCanExecuteChanged();
    }

    // What the manifest offers: the first download, or a newer one than the one downloaded (not
    // asked when another ffmpeg is in use).
    async Task CheckOfferAsync()
    {
        if (tools.Rid == null || Ffmpeg is { Origin: not FfmpegOrigin.Downloaded }) return;
        try
        {
            var manifest = await tools.FetchManifestAsync();
            Offer = tools.Offers(manifest) ? manifest : null;
        }
        catch (Exception e) when (e is HttpRequestException or ToolsManifestException or IOException or TaskCanceledException)
        {
            if (NeedsFfmpeg) DownloadError = Loc.F("FfmpegManifestFailed", e.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    async Task DownloadFfmpeg()
    {
        DownloadError = null;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadText = Loc.T("FfmpegChecking");
        download = new CancellationTokenSource();
        try
        {
            var manifest = Offer ?? await tools.FetchManifestAsync(download.Token);
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                DownloadProgress = p.Total > 0 ? (double)p.Done / p.Total : 0;
                DownloadText = Loc.F("FfmpegDownloading", Size(p.Done), Size(p.Total));
            });
            await tools.InstallAsync(manifest, progress, download.Token);
            Offer = null;
            // The one downloaded is the one used from now on.
            if (settings.Ffmpeg != null) { settings.Ffmpeg = null; settings.Save(); }
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or ToolsManifestException or FfmpegToolsException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            DownloadError = Loc.F("FfmpegDownloadFailed", e.Message);
        }
        finally
        {
            IsDownloading = false;
            download.Dispose();
            download = null;
            RefreshFfmpeg();
        }
    }

    [RelayCommand]
    void CancelDownload() => download?.Cancel();

    [RelayCommand]
    async Task ChooseFfmpeg()
    {
        if (await dialogs.PickFileAsync(Loc.T("VideoFfmpeg"), "ffmpeg", FfmpegTools.Exe("ffmpeg")) is not { } file) return;
        settings.Ffmpeg = file;
        settings.Save();
        RefreshFfmpeg();
    }

    // Back to the downloaded one (or the system's) after one was chosen.
    [RelayCommand]
    void ForgetChosenFfmpeg()
    {
        settings.Ffmpeg = null;
        settings.Save();
        RefreshFfmpeg();
    }

    [RelayCommand]
    async Task RemoveDownloadedFfmpeg()
    {
        if (!await dialogs.ConfirmAsync(Loc.T("FfmpegRemoveTitle"), Loc.T("FfmpegRemoveMessage"), Loc.T("Delete"))) return;
        try { tools.Remove(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await dialogs.MessageAsync(Loc.T("FfmpegRemoveTitle"), e.Message); }
        RefreshFfmpeg();
        _ = CheckOfferAsync();
    }

    // ── Files ───────────────────────────────────────────────────────────────

    [RelayCommand]
    async Task AddFiles()
    {
        var files = await dialogs.PickFilesAsync(Loc.T("TranscodeAddTitle"));
        if (files.Count > 0) Add(files);
    }

    public void Add(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            if (!File.Exists(file) || Items.Any(i => string.Equals(i.Path, file, StringComparison.Ordinal))) continue;
            var item = new TranscodeItem { Path = file };
            Items.Add(item);
            _ = ProbeAsync(item);
        }
        Selected ??= Items.FirstOrDefault();
    }

    async Task ProbeAsync(TranscodeItem item)
    {
        if (Ffmpeg?.Ffprobe is not { } ffprobe)
        {
            item.Status = TranscodeStatus.Unreadable;
            item.Error = Ffmpeg == null ? Loc.T("FfmpegMissing") : Loc.T("FfprobeMissing");
            item.Description = "";
            return;
        }
        try
        {
            var info = await FfmpegRunner.ProbeAsync(ffprobe, item.Path);
            item.Info = info;
            item.Description = Describe(info, item.Path);
            item.Status = info.Audio.Count == 0 && info.Video == null ? TranscodeStatus.Unreadable : TranscodeStatus.Ready;
            if (item.Status == TranscodeStatus.Unreadable) item.Error = Loc.T("TranscodeNothingInside");
            // A video's file gets a video format the first time one is added.
            if (Items.Count == 1 && info.Video == null && TranscodeFormat.Find(Format.Value).IsVideo) Format = Formats.First(f => f.Value == "ogg");
        }
        catch (TranscodeException e)
        {
            item.Status = TranscodeStatus.Unreadable;
            item.Error = e.Message;
            item.Description = "";
        }
        if (item == Selected) { RebuildTracks(); Refresh(); _ = LoadPreviewAsync(item); }
        StartCommand.NotifyCanExecuteChanged();
    }

    static string Describe(MediaInfo info, string path)
    {
        var parts = new List<string> { System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() };
        if (info.Duration is > 0 and var d && double.IsFinite(d)) parts.Add(TranscodeItem.Clock(d));
        if (info.Video is { } v) parts.Add($"{v.Codec.ToUpperInvariant()} {v.Width}×{v.Height}" + (v.Fps is { } f ? $" {f:0.##}fps" : ""));
        if (info.FirstUsableAudio >= 0 && info.Audio[info.FirstUsableAudio] is var a)
            parts.Add($"{a.Codec.ToUpperInvariant()}" + (a.SampleRate is { } r ? $" {r / 1000.0:0.###}kHz" : "") + " " + ChannelName(a));
        if (info.Audio.Count > 1) parts.Add(Loc.F("TranscodeTracks", info.Audio.Count));
        var bad = info.Audio.Where(t => t.Unsupported).Select(t => t.Codec.ToUpperInvariant()).ToList();
        if (bad.Count > 0) parts.Add(Loc.F("TranscodeUndecodable", string.Join(", ", bad)));
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    static string ChannelName(AudioTrack a) => a.Channels switch
    {
        1 => Loc.T("Mono"),
        2 => Loc.T("Stereo"),
        0 => "",
        var n => a.Layout.Length > 0 ? a.Layout : Loc.F("ChannelsN", n),
    };

    [RelayCommand]
    void Remove(TranscodeItem? item)
    {
        item ??= Selected;
        if (item == null || item.IsRunning) return;
        int index = Items.IndexOf(item);
        Items.Remove(item);
        if (Selected == null || Selected == item) Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    [RelayCommand]
    void Clear()
    {
        if (IsRunning) return;
        Items.Clear();
        Selected = null;
    }

    partial void OnSelectedChanged(TranscodeItem? oldValue, TranscodeItem? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= SelectedTrimChanged;
        if (newValue != null) newValue.PropertyChanged += SelectedTrimChanged;
        RebuildTracks();
        Refresh();
        _ = LoadPreviewAsync(newValue);
    }

    void SelectedTrimChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranscodeItem.TrimStart) or nameof(TranscodeItem.TrimEnd)) Refresh();
    }

    [RelayCommand]
    void ResetTrim()
    {
        if (Selected == null) return;
        Selected.TrimStart = 0;
        Selected.TrimEnd = null;
    }

    [RelayCommand]
    async Task BrowseOutput()
    {
        if (await dialogs.PickFolderAsync(Loc.T("TranscodeOutput")) is { } folder) OutputFolder = folder;
    }

    [RelayCommand]
    void SameFolder() => OutputFolder = "";

    partial void OnOutputFolderChanged(string value)
    {
        settings.TranscodeFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Refresh();
    }

    // ── Choices ─────────────────────────────────────────────────────────────

    public TranscodeOptions Options => new()
    {
        Format = Format.Value,
        AudioTrack = AudioTrack?.Value ?? 0,
        AudioCodec = AudioCodec?.Value ?? TranscodeOptions.Auto,
        Vbr = RateMode?.Value,
        Bitrate = Bitrate?.Value,
        Quality = Quality?.Value,
        FlacLevel = FlacLevel.Value,
        PcmDepth = PcmDepth.Value,
        SampleRate = SampleRate.Value,
        Channels = Channels.Value,
        VolumeDb = (double)VolumeDb,
        Normalize = Normalize,
        FadeIn = (double)FadeIn,
        FadeOut = (double)FadeOut,
        KeepMetadata = KeepMetadata,
        VideoCodec = VideoCodec?.Value ?? TranscodeOptions.Auto,
        Crf = (int)Crf,
        Preset = Preset.Value,
        Height = Height.Value,
        Fps = Fps.Value,
        Extra = Extra,
    };

    partial void OnFormatChanged(Choice<string> value)
    {
        settings.TranscodeFormat = value.Value;
        RebuildCodecs(keep: false);
    }

    partial void OnAudioCodecChanged(Choice<string>? value) => RebuildCodecFields(keep: true);
    partial void OnVideoCodecChanged(Choice<string>? value) => RebuildCodecFields(keep: true);
    partial void OnAudioTrackChanged(Choice<int>? value) => Refresh();
    partial void OnRateModeChanged(Choice<bool>? value) => RebuildCodecFields(keep: true);
    partial void OnBitrateChanged(Choice<int>? value) => Refresh();
    partial void OnQualityChanged(Choice<int>? value) => Refresh();
    partial void OnFlacLevelChanged(Choice<int> value) => Refresh();
    partial void OnPcmDepthChanged(Choice<string> value) => Refresh();
    partial void OnSampleRateChanged(Choice<int?> value) => Refresh();
    partial void OnChannelsChanged(Choice<int?> value) => Refresh();
    partial void OnVolumeDbChanged(decimal value) => Refresh();
    partial void OnNormalizeChanged(bool value) => Refresh();
    partial void OnFadeInChanged(decimal value) => Refresh();
    partial void OnFadeOutChanged(decimal value) => Refresh();
    partial void OnKeepMetadataChanged(bool value) => Refresh();
    partial void OnCrfChanged(decimal value) => Refresh();
    partial void OnPresetChanged(Choice<string> value) => Refresh();
    partial void OnHeightChanged(Choice<int?> value) => Refresh();
    partial void OnFpsChanged(Choice<int?> value) => Refresh();
    partial void OnExtraChanged(string value) => Refresh();

    void RebuildTracks()
    {
        building = true;
        var info = Selected?.Info;
        int previous = AudioTrack?.Value ?? 0;
        AudioTracks = info == null || info.Audio.Count == 0
            ? new[] { new Choice<int>(0, "#1") }
            : info.Audio.Select((a, i) => new Choice<int>(i, $"#{i + 1} {a.Codec.ToUpperInvariant()}" + (a.Language.Length > 0 ? $" · {a.Language}" : "")
                + (ChannelName(a) is { Length: > 0 } ch ? $" · {ch}" : "") + (a.Unsupported ? " · " + Loc.T("TranscodeCannotDecode") : ""))).ToList();
        int usable = info == null ? 0 : Math.Max(0, info.FirstUsableAudio);
        AudioTrack = AudioTracks.FirstOrDefault(t => t.Value == previous && !(info?.Audio.ElementAtOrDefault(previous)?.Unsupported ?? false)) ?? AudioTracks.ElementAtOrDefault(usable) ?? AudioTracks[0];
        building = false;
    }

    void RebuildCodecs(bool keep)
    {
        building = true;
        var f = TranscodeFormat.Find(Format.Value);
        IsVideo = f.IsVideo;
        string? audio = keep ? AudioCodec?.Value : null, video = keep ? VideoCodec?.Value : null;
        AudioCodecs = new[] { new Choice<string>(TranscodeOptions.Auto, Loc.T("CodecAuto")) }
            .Concat(f.AudioCodecs.Select(c => new Choice<string>(c, Codecs.Name(c))))
            .Append(new Choice<string>(TranscodeOptions.Copy, Loc.T("CodecCopy"))).ToList();
        AudioCodec = AudioCodecs.FirstOrDefault(c => c.Value == audio) ?? AudioCodecs[0];
        VideoCodecs = !f.IsVideo ? Array.Empty<Choice<string>>() : new[] { new Choice<string>(TranscodeOptions.Auto, Loc.T("CodecAuto")) }
            .Concat(f.VideoCodecs.Select(c => new Choice<string>(c, Codecs.Name(c))))
            .Append(new Choice<string>(TranscodeOptions.Copy, Loc.T("CodecCopy"))).ToList();
        VideoCodec = VideoCodecs.FirstOrDefault(c => c.Value == video) ?? VideoCodecs.FirstOrDefault();
        building = false;
        RebuildCodecFields(keep: false);
    }

    // The fields of the encoders in effect: shown or not, and their lists.
    void RebuildCodecFields(bool keep)
    {
        if (building) return;
        building = true;
        var options = Options;
        string c = options.EffectiveAudioCodec;
        bool audioCopy = options.AudioCodec == TranscodeOptions.Copy;
        var rates = Codecs.Bitrates(c);
        var qualityList = Codecs.Qualities(c);
        ShowAudioEncoding = !audioCopy;
        ShowRateMode = !audioCopy && rates != null && qualityList != null;
        if (!keep || !ShowRateMode) RateMode = RateModes.First(m => m.Value == Codecs.DefaultVbr(c));
        bool vbr = qualityList != null && (rates == null || RateMode?.Value == true);
        ShowBitrate = !audioCopy && rates != null && !vbr;
        ShowQuality = !audioCopy && qualityList != null && vbr;
        int? oldBitrate = Bitrate?.Value, oldQuality = Quality?.Value;
        Bitrates = rates?.Select(b => new Choice<int>(b, $"{b} kbps")).ToList() ?? (IReadOnlyList<Choice<int>>)Array.Empty<Choice<int>>();
        Bitrate = Bitrates.FirstOrDefault(b => keep && b.Value == oldBitrate) ?? Bitrates.FirstOrDefault(b => b.Value == Codecs.DefaultBitrate(c));
        Qualities = qualityList?.Select(q => new Choice<int>(q.Value, q.Label)).ToList() ?? (IReadOnlyList<Choice<int>>)Array.Empty<Choice<int>>();
        Quality = Qualities.FirstOrDefault(q => keep && q.Value == oldQuality) ?? Qualities.FirstOrDefault(q => q.Value == Codecs.DefaultQuality(c));
        ShowFlac = !audioCopy && c == "flac";
        ShowDepth = !audioCopy && c == "pcm";
        ShowSampleRate = !audioCopy && c != "libopus";
        string? v = options.EffectiveVideoCodec;
        ShowVideoEncoding = IsVideo && options.VideoCodec != TranscodeOptions.Copy;
        ShowPreset = ShowVideoEncoding && v == "libx264";
        if (v != null)
        {
            CrfMax = Codecs.MaxCrf(v);
            if (!keep || Crf > CrfMax) Crf = Codecs.DefaultCrf(v);
        }
        building = false;
        Refresh();
    }

    // The plan and the command for the selected file.
    void Refresh()
    {
        if (building) return;
        var options = Options;
        if (Selected?.Info is not { } info)
        {
            Plan = Selected == null ? "" : Selected.StatusText;
            CommandLine = "";
            return;
        }
        var d = TranscodePlan.Decide(info, options);
        var format = options.TargetFormat;
        var said = new List<string>();
        if (!format.IsVideo && info.Video != null) said.Add(Loc.T("PlanExtract"));
        if (format.IsVideo && info.Video == null) said.Add(Loc.T("PlanNoPicture"));
        if (d.Source == null) said.Add(info.Audio.Count == 0 ? Loc.T("PlanNoAudio") : Loc.T("PlanNoUsableAudio"));
        else said.Add(d.AudioCopy ? Loc.T("PlanAudioCopy") : Loc.F("PlanAudioEncode", Codecs.Name(options.EffectiveAudioCodec)));
        if (format.IsVideo && info.Video != null) said.Add(d.VideoCopy ? Loc.T("PlanVideoCopy") : Loc.F("PlanVideoEncode", Codecs.Name(options.EffectiveVideoCodec!)));
        Plan = string.Join(Loc.T("PlanSeparator"), said);
        CommandLine = TranscodePlan.CommandLine(TranscodePlan.Arguments(info, options, Selected.Trim, Selected.Path, OutputFor(Selected, null)));
    }

    string OutputFor(TranscodeItem item, ISet<string>? taken)
    {
        string folder = string.IsNullOrWhiteSpace(OutputFolder) ? System.IO.Path.GetDirectoryName(item.Path) ?? "" : OutputFolder.Trim();
        return TranscodePlan.OutputPath(item.Path, folder, TranscodeFormat.Find(Format.Value), taken);
    }

    // ── Running ─────────────────────────────────────────────────────────────

    bool CanStart => !IsRunning && HasFfmpeg && Items.Any(i => i.Info != null && i.Status != TranscodeStatus.Unreadable);

    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task Start()
    {
        if (Ffmpeg is not { } ffmpeg) return;
        StopPreview();
        settings.Save();
        var options = Options;
        var todo = Items.Where(i => i.Info != null && i.Status != TranscodeStatus.Unreadable).ToList();
        foreach (var item in todo) { item.Status = TranscodeStatus.Waiting; item.Progress = 0; item.Error = null; item.Output = null; }
        IsRunning = true;
        run = new CancellationTokenSource();
        var taken = new HashSet<string>();
        int done = 0, failed = 0;
        try
        {
            for (int n = 0; n < todo.Count; n++)
            {
                var item = todo[n];
                if (run.IsCancellationRequested) { item.Status = TranscodeStatus.Cancelled; continue; }
                RunText = Loc.F("TranscodeRunningN", n + 1, todo.Count);
                string output = OutputFor(item, taken);
                // Not over a file that is there already unless asked.
                if (File.Exists(output) && !await dialogs.ConfirmAsync(Loc.T("TranscodeOverwriteTitle"), Loc.F("TranscodeOverwrite", output), Loc.T("TranscodeOverwriteYes")))
                {
                    item.Status = TranscodeStatus.Cancelled;
                    continue;
                }
                item.Status = TranscodeStatus.Running;
                var trim = item.Trim;
                double length = (trim.End ?? item.Duration) - trim.Start;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
                    var args = TranscodePlan.Arguments(item.Info!, options, trim, item.Path, output);
                    await FfmpegRunner.ConvertAsync(ffmpeg.Ffmpeg, args, output, length, new Progress<double>(p => item.Progress = p), run.Token);
                    item.Output = output;
                    item.Status = TranscodeStatus.Done;
                    done++;
                    converted?.Invoke(output);
                }
                catch (OperationCanceledException) { item.Status = TranscodeStatus.Cancelled; }
                catch (Exception e) when (e is TranscodeException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    item.Error = e.Message;
                    item.Status = TranscodeStatus.Failed;
                    failed++;
                }
            }
        }
        finally
        {
            RunText = run.IsCancellationRequested ? Loc.F("TranscodeStopped", done) : failed > 0 ? Loc.F("TranscodeFinishedFailed", done, failed) : Loc.F("TranscodeFinished", done);
            run.Dispose();
            run = null;
            IsRunning = false;
        }
    }

    partial void OnIsRunningChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    void Cancel() => run?.Cancel();

    [RelayCommand]
    void Back()
    {
        if (IsRunning) return;
        Close();
        back();
    }

    public void Close()
    {
        download?.Cancel();
        StopPreview();
        player?.Dispose();
        player = null;
        settings.Save();
    }

    // ── Hearing it ──────────────────────────────────────────────────────────

    async Task LoadPreviewAsync(TranscodeItem? item)
    {
        int load = ++previewLoad;
        StopPreview();
        player?.Dispose();
        player = null;
        previewAudio = null;
        Waveform = null;
        PreviewPosition = item?.TrimStart ?? 0;
        PreviewText = "";
        if (item?.Info is not { } info || Ffmpeg is not { } ffmpeg) return;
        var d = TranscodePlan.Decide(info, Options);
        if (d.Source == null) { PreviewText = Loc.T("PlanNoUsableAudio"); return; }
        PreviewLoading = true;
        PreviewText = Loc.T("PreviewLoading");
        try
        {
            const int rate = 44100;
            var samples = await FfmpegRunner.DecodeAsync(ffmpeg.Ffmpeg, item.Path, d.AudioIndex, rate, 2);
            if (load != previewLoad) return;
            var audio = new PcmAudio { SampleRate = rate, Channels = 2, Samples = samples };
            var wave = await Task.Run(() => new Waveform(audio, Math.Max(0.005, audio.Duration / 4000)));
            if (load != previewLoad) return;
            previewAudio = audio;
            Waveform = wave;
            PreviewText = "";
        }
        catch (Exception e) when (e is TranscodeException or IOException or OutOfMemoryException or System.ComponentModel.Win32Exception)
        {
            if (load == previewLoad) PreviewText = e.Message;
        }
        finally
        {
            if (load == previewLoad) PreviewLoading = false;
        }
    }

    partial void OnWaveformChanged(Waveform? value)
    {
        OnPropertyChanged(nameof(CanPreview));
        TogglePreviewCommand.NotifyCanExecuteChanged();
    }

    // Plays the part to convert from where the position is (from its start when outside it).
    [RelayCommand(CanExecute = nameof(CanPreview))]
    void TogglePreview()
    {
        if (IsPreviewPlaying) { StopPreview(); return; }
        if (previewAudio == null || Selected == null) return;
        player ??= OpenAlPlayer.Open(previewAudio);
        if (player == null) { PreviewText = Loc.T("NoAudioDevice").Trim(); return; }
        player.Volume = (float)settings.Volume;
        double end = Selected.Trim.End ?? previewAudio.Duration;
        if (PreviewPosition < Selected.TrimStart || PreviewPosition >= end - 0.05) PreviewPosition = Selected.TrimStart;
        player.Seek(PreviewPosition);
        player.Play();
        IsPreviewPlaying = true;
        previewTimer.Start();
    }

    public void SeekPreview(double seconds)
    {
        PreviewPosition = Math.Clamp(seconds, 0, previewAudio?.Duration ?? 0);
        if (IsPreviewPlaying) player?.Seek(PreviewPosition);
    }

    void PreviewTick()
    {
        if (player == null || Selected == null) { StopPreview(); return; }
        PreviewPosition = player.Position;
        double end = Selected.Trim.End ?? player.Duration;
        if (!player.IsPlaying || PreviewPosition >= end) { StopPreview(); PreviewPosition = Math.Min(PreviewPosition, end); }
    }

    void StopPreview()
    {
        previewTimer.Stop();
        player?.Pause();
        IsPreviewPlaying = false;
    }

    // The trim set from the position: its start or its end.
    [RelayCommand]
    void TrimStartHere()
    {
        if (Selected == null) return;
        double end = Selected.TrimEnd ?? Selected.Duration;
        Selected.TrimStart = Math.Min(PreviewPosition, Math.Max(0, end - 0.1));
    }

    [RelayCommand]
    void TrimEndHere()
    {
        if (Selected == null) return;
        Selected.TrimEnd = Math.Max(PreviewPosition, Selected.TrimStart + 0.1);
    }
}
