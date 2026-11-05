using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CharTaiko.App.Localization;
using CharTaiko.Audio;
using CharTaiko.Core.Localization;
using CharTaiko.Core.Settings;

namespace CharTaiko.App.Views;

// The settings, edited on a copy: OK saves them and every view takes them over; Cancel leaves
// them as they were. Closes with true when they were saved.
public partial class SettingsDialog : Window
{
    static readonly string[] LanguageValues = { "system", "en", "zh", "ja" };
    static readonly string[] MessageValues = { "ui", "en", "zh", "ja" };

    readonly AppSettings? settings;

    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(AppSettings settings) : this()
    {
        this.settings = settings;
        var s = settings.Clone();
        LanguageBox.ItemsSource = LanguageValues.Select(v => v == "system" ? Loc.T("FollowSystem") : Loc.NativeName(v)).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, s.Language));
        MessageBox.ItemsSource = MessageValues.Select(Names.MessageLanguage).ToList();
        MessageBox.SelectedIndex = Math.Max(0, Array.IndexOf(MessageValues, s.MessageLanguage));
        FolderBox.Text = s.ProjectsFolderOrDefault;
        FontSizeBox.Value = (decimal)s.FontSize;
        HintsBox.IsChecked = s.ShowHints;
        WrapBox.IsChecked = s.WordWrap;
        DivisionBox.ItemsSource = AppSettings.Divisions.Select(d => "1/" + d).ToList();
        DivisionBox.SelectedIndex = Math.Max(0, AppSettings.Divisions.ToList().IndexOf(s.Division));
        VolumeBox.Value = s.Volume;
        HitsBox.IsChecked = s.HitSounds;
        HitVolumeBox.Value = s.HitVolume;
        var sets = HitSoundSets.Names;
        HitSetBox.ItemsSource = sets.Count > 0 ? sets : new[] { "—" };
        HitSetBox.SelectedIndex = Math.Clamp(s.HitSoundSet, 0, Math.Max(0, sets.Count - 1));
        LatencyBox.Value = (decimal)s.LatencyMs;
        FileText.Text = s.File == null ? "" : Loc.F("SettingsFile", s.File);
    }

    async void BrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("ProjectsFolder"), AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder) FolderBox.Text = folder;
    }

    // The values in the dialog, on a copy of the settings.
    public AppSettings Edited()
    {
        var s = (settings ?? new AppSettings()).Clone();
        s.Language = LanguageValues[Math.Max(0, LanguageBox.SelectedIndex)];
        s.MessageLanguage = MessageValues[Math.Max(0, MessageBox.SelectedIndex)];
        string folder = FolderBox.Text?.Trim() ?? "";
        // The default stays unset, so it follows the Documents folder wherever that is.
        s.ProjectsFolder = folder.Length == 0 || folder == AppSettings.DefaultProjectsFolder ? null : folder;
        s.FontSize = (double)(FontSizeBox.Value ?? 14);
        s.ShowHints = HintsBox.IsChecked == true;
        s.WordWrap = WrapBox.IsChecked == true;
        s.Division = AppSettings.Divisions[Math.Max(0, DivisionBox.SelectedIndex)];
        s.Volume = VolumeBox.Value;
        s.HitSounds = HitsBox.IsChecked == true;
        s.HitVolume = HitVolumeBox.Value;
        s.HitSoundSet = Math.Max(0, HitSetBox.SelectedIndex);
        s.LatencyMs = (double)(LatencyBox.Value ?? 0);
        return s;
    }

    void Accept(object? sender, RoutedEventArgs e)
    {
        settings?.Apply(Edited());
        Close(true);
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
