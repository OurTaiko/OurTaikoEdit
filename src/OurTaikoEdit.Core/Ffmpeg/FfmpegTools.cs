using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OurTaikoEdit.Core.Ffmpeg;

public enum FfmpegOrigin { Chosen, Downloaded, System }

// An ffmpeg the app can run, its ffprobe beside it when there is one, and where it came from.
public sealed record FfmpegLocation(string Ffmpeg, string? Ffprobe, FfmpegOrigin Origin);

// The ffmpeg downloaded into the app's data folder: tools/ffmpeg/<version>/, with the programs,
// their licence and an installed.json saying which manifest they came from.
public sealed record InstalledFfmpeg(string Version, DateTimeOffset Published, string Folder)
{
    public string Ffmpeg => Path.Combine(Folder, FfmpegTools.Exe("ffmpeg"));
    public string Ffprobe => Path.Combine(Folder, FfmpegTools.Exe("ffprobe"));
}

// Finding ffmpeg (the one chosen, else the one downloaded, else one installed on the system) and
// downloading the app's own: a manifest signed by the release key says what to get for this
// platform; the package is checked against its size and SHA-256, unpacked beside the old one,
// tried, and only then put in its place.
public sealed class FfmpegTools
{
    public static readonly Uri DefaultManifest = new("https://distrib.sparkbarry.com/OurTaikoEdit/tools/ffmpeg/manifest.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    readonly HttpClient http;
    readonly Uri manifestUrl;
    readonly IReadOnlyDictionary<string, string>? keys;

    // Where the downloaded ffmpeg goes: <data folder>/tools/ffmpeg.
    public string Folder { get; }
    // The platform's package name in the manifest; null where there is none (Linux, Intel Macs).
    public string? Rid { get; }

    public FfmpegTools(string dataFolder, HttpClient? http = null, Uri? manifest = null, IReadOnlyDictionary<string, string>? keys = null, string? rid = "")
    {
        Folder = Path.Combine(dataFolder, "tools", "ffmpeg");
        this.http = http ?? SharedHttp;
        manifestUrl = manifest ?? DefaultManifest;
        this.keys = keys;
        Rid = rid == "" ? CurrentRid : rid;
    }

    public static string? CurrentRid
    {
        get
        {
            if (OperatingSystem.IsMacOS()) return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : null;
            // Windows on Arm runs the x64 build.
            if (OperatingSystem.IsWindows()) return RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? "win-x64" : null;
            return null;
        }
    }

    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    // The newest downloaded ffmpeg that is all there; null when none is.
    public InstalledFfmpeg? Installed
    {
        get
        {
            if (!Directory.Exists(Folder)) return null;
            InstalledFfmpeg? best = null;
            foreach (string dir in Directory.EnumerateDirectories(Folder))
            {
                if (dir.EndsWith(".partial", StringComparison.Ordinal)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<InstalledRecord>(File.ReadAllText(Path.Combine(dir, "installed.json")));
                    if (record?.Version == null) continue;
                    var installed = new InstalledFfmpeg(record.Version, record.Published, dir);
                    if (!File.Exists(installed.Ffmpeg)) continue;
                    if (best == null || installed.Published > best.Published) best = installed;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
            return best;
        }
    }

    // The ffmpeg to use: the one chosen (null when it is not there), else the downloaded one, else
    // one on the PATH or where package managers put it (an app opened from the Finder does not get
    // the shell's PATH); null when there is none.
    public FfmpegLocation? Find(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            string path = chosen.Trim();
            return File.Exists(path) ? new FfmpegLocation(path, Beside(path), FfmpegOrigin.Chosen) : null;
        }
        if (Installed is { } installed) return new FfmpegLocation(installed.Ffmpeg, File.Exists(installed.Ffprobe) ? installed.Ffprobe : null, FfmpegOrigin.Downloaded);
        return FindOnSystem();
    }

    public static FfmpegLocation? FindOnSystem()
    {
        var folders = new List<string> { AppContext.BaseDirectory };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (!OperatingSystem.IsWindows()) folders.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/opt/local/bin", "/snap/bin" });
        foreach (string folder in folders)
        {
            try
            {
                string path = Path.Combine(folder, Exe("ffmpeg"));
                if (File.Exists(path)) return new FfmpegLocation(path, Beside(path), FfmpegOrigin.System);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static string? Beside(string ffmpeg)
    {
        string probe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", Exe("ffprobe"));
        return File.Exists(probe) ? probe : null;
    }

    public async Task<ToolsManifest> FetchManifestAsync(CancellationToken cancel = default)
    {
        string json;
        if (manifestUrl.IsFile) json = await File.ReadAllTextAsync(manifestUrl.LocalPath, cancel);
        else
        {
            using var response = await http.GetAsync(manifestUrl, cancel);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancel);
        }
        return ToolsManifest.Parse(json, manifestUrl, keys);
    }

    // Whether a manifest offers something to download here: a package for this platform, and
    // newer than what is installed (an older one is never taken, so an old manifest served
    // again cannot bring back an old ffmpeg).
    public bool Offers(ToolsManifest manifest)
        => manifest.For(Rid) != null && (Installed is not { } installed || manifest.Published > installed.Published);

    // Downloads, checks, unpacks and tries the manifest's package for this platform, puts it in
    // place, and removes the older ones. Progress is bytes downloaded of the package's size.
    public async Task<InstalledFfmpeg> InstallAsync(ToolsManifest manifest, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancel = default)
    {
        var package = manifest.For(Rid) ?? throw new FfmpegToolsException($"There is no ffmpeg for this platform ({Rid ?? RuntimeInformation.RuntimeIdentifier}).");
        if (manifest.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || manifest.Version is "." or "..")
            throw new FfmpegToolsException($"Bad version {manifest.Version}.");
        Directory.CreateDirectory(Folder);
        string zip = Path.Combine(Folder, $"download-{Guid.NewGuid():N}.zip");
        string partial = Path.Combine(Folder, manifest.Version + ".partial");
        string target = Path.Combine(Folder, manifest.Version);
        try
        {
            await DownloadAsync(package, zip, progress, cancel);
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            ZipFile.ExtractToDirectory(zip, partial);
            var installed = new InstalledFfmpeg(manifest.Version, manifest.Published, partial);
            if (!File.Exists(installed.Ffmpeg)) throw new FfmpegToolsException("The package has no ffmpeg in it.");
            if (!OperatingSystem.IsWindows())
                foreach (string program in new[] { installed.Ffmpeg, installed.Ffprobe }.Where(File.Exists))
                    File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            await TryAsync(installed.Ffmpeg, cancel);
            File.WriteAllText(Path.Combine(partial, "installed.json"), JsonSerializer.Serialize(new InstalledRecord { Version = manifest.Version, Published = manifest.Published }, Json));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(partial, target);
        }
        finally
        {
            TryDelete(zip);
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (string dir in Directory.EnumerateDirectories(Folder))
            if (!string.Equals(dir, target, StringComparison.Ordinal))
                try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new InstalledFfmpeg(manifest.Version, manifest.Published, target);
    }

    // Removes every downloaded ffmpeg.
    public void Remove()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
    }

    async Task DownloadAsync(ToolsPackage package, string file, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        Stream source;
        HttpResponseMessage? response = null;
        if (package.Url.IsFile) source = File.OpenRead(package.Url.LocalPath);
        else
        {
            response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            source = await response.Content.ReadAsStreamAsync(cancel);
        }
        try
        {
            await using (var output = File.Create(file))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                progress?.Report((0, package.Size));
                while ((n = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    done += n;
                    if (done > package.Size) throw new FfmpegToolsException("The download is larger than the manifest says.");
                    await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                    progress?.Report((done, package.Size));
                }
                if (done != package.Size) throw new FfmpegToolsException($"The download ended at {done} of {package.Size} bytes.");
            }
            await using var check = File.OpenRead(file);
            if (ToolsManifest.Sha256(check) != package.Sha256) throw new FfmpegToolsException("The download does not match its SHA-256.");
        }
        finally
        {
            await source.DisposeAsync();
            response?.Dispose();
        }
    }

    // Runs `ffmpeg -version`: the program must start and say so.
    static async Task TryAsync(string ffmpeg, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-version");
        try
        {
            using var process = Process.Start(info) ?? throw new FfmpegToolsException("ffmpeg did not start.");
            var error = process.StandardError.ReadToEndAsync(cancel);
            string output = await process.StandardOutput.ReadToEndAsync(cancel);
            await error;
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0 || !output.StartsWith("ffmpeg version", StringComparison.Ordinal))
                throw new FfmpegToolsException($"The downloaded ffmpeg does not run (exit code {process.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception e) { throw new FfmpegToolsException($"The downloaded ffmpeg does not run: {e.Message}"); }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    sealed class InstalledRecord
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("published")] public DateTimeOffset Published { get; set; }
    }
}

public sealed class FfmpegToolsException(string message) : Exception(message);
