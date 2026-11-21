using System.IO.Compression;
using System.Text.Json.Nodes;
using OurTaikoEdit.Core.Ffmpeg;

namespace OurTaikoEdit.Tests;

// The signed ffmpeg manifest and the download into the data folder (from file URLs: no network).
public class FfmpegToolsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-ffmpeg-" + Guid.NewGuid().ToString("N"));
    readonly (string Private, string Public) key = ToolsManifest.GenerateKeyPair();
    IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string> { ["test"] = key.Public };
    static readonly Uri Location = new("https://example.invalid/tools/ffmpeg/manifest.json");

    public FfmpegToolsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    JsonObject Manifest(string version = "8.0-1", string published = "2026-11-20T00:00:00Z", string url = "8.0-1/osx-arm64/ffmpeg.zip", string sha = "", long size = 10)
        => new()
        {
            ["kind"] = ToolsManifest.Kind,
            ["version"] = version,
            ["published"] = published,
            ["source"] = $"{version}/source/",
            ["platforms"] = new JsonObject
            {
                ["osx-arm64"] = new JsonObject { ["url"] = url, ["sha256"] = sha.Length > 0 ? sha : new string('a', 64), ["size"] = size },
            },
        };

    string Signed(JsonObject manifest, string? privateKey = null, string keyId = "test")
    {
        ToolsManifest.Sign(manifest, privateKey ?? key.Private, keyId);
        return manifest.ToJsonString();
    }

    [Fact]
    public void ASignedManifestIsReadWithItsUrlsAgainstItsOwn()
    {
        var m = ToolsManifest.Parse(Signed(Manifest()), Location, Keys);
        Assert.Equal("8.0-1", m.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), m.Published);
        Assert.Equal("https://example.invalid/tools/ffmpeg/8.0-1/osx-arm64/ffmpeg.zip", m.For("osx-arm64")!.Url.ToString());
        Assert.Equal("https://example.invalid/tools/ffmpeg/8.0-1/source/", m.Source.ToString());
        Assert.Null(m.For("win-x64"));
    }

    [Fact]
    public void AChangedUnsignedOrStrangelySignedManifestIsRejected()
    {
        // Changed after signing: another file to download.
        var changed = JsonNode.Parse(Signed(Manifest()))!.AsObject();
        changed["platforms"]!["osx-arm64"]!["url"] = "https://evil.invalid/ffmpeg.zip";
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(changed.ToJsonString(), Location, Keys));
        // Not signed.
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(Manifest().ToJsonString(), Location, Keys));
        // Signed by a key the app does not know, even under a name it does.
        var other = ToolsManifest.GenerateKeyPair();
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(Signed(Manifest(), other.PrivateKey), Location, Keys));
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(Signed(Manifest(), keyId: "nobody"), Location, Keys));
        // Signed, but not an ffmpeg manifest (another kind of signed file).
        var game = Manifest();
        game["kind"] = "rhythmhub-release";
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(Signed(game), Location, Keys));
        // A URL that is not http(s).
        Assert.Throws<ToolsManifestException>(() => ToolsManifest.Parse(Signed(Manifest(url: "ftp://example.invalid/x.zip")), Location, Keys));
    }

    [Fact]
    public void TheAppTrustsOneKeyOfTheRightSize()
    {
        var (id, publicKey) = Assert.Single(ToolsManifest.TrustedKeys);
        Assert.Equal("ourtaikoedit-tools-2026", id);
        Assert.Equal(32, Convert.FromBase64String(publicKey).Length);
    }

    // A package with a stand-in ffmpeg (a script that says what ffmpeg says) where scripts run.
    (Uri Manifest, string Zip) Publish(string version, string published, string say = "ffmpeg version test")
    {
        string feed = Path.Combine(root, "feed");
        string dir = Path.Combine(feed, version, "osx-arm64");
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "ffmpeg.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("ffmpeg");
            entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
            using (var w = new StreamWriter(entry.Open())) w.Write($"#!/bin/sh\necho '{say}'\n");
            using (var w = new StreamWriter(archive.CreateEntry("ffprobe").Open())) w.Write("#!/bin/sh\n");
            using (var w = new StreamWriter(archive.CreateEntry("LICENSE.txt").Open())) w.Write("GPL");
        }
        string sha;
        using (var s = File.OpenRead(zip)) sha = ToolsManifest.Sha256(s);
        string manifest = Path.Combine(feed, "manifest.json");
        File.WriteAllText(manifest, Signed(Manifest(version, published, $"{version}/osx-arm64/ffmpeg.zip", sha, new FileInfo(zip).Length)));
        return (new Uri(manifest), zip);
    }

    FfmpegTools Tools(Uri manifest) => new(Path.Combine(root, "data"), manifest: manifest, keys: Keys, rid: "osx-arm64");

    [Fact]
    public async Task ThePackageIsDownloadedCheckedAndPutInPlace()
    {
        if (OperatingSystem.IsWindows()) return;
        var (manifestUrl, _) = Publish("8.0-1", "2026-11-20T00:00:00Z");
        var tools = Tools(manifestUrl);
        Assert.Null(tools.Installed);
        var manifest = await tools.FetchManifestAsync();
        Assert.True(tools.Offers(manifest));
        var reports = new List<(long, long)>();
        var installed = await tools.InstallAsync(manifest, new SyncProgress<(long, long)>(reports.Add));
        Assert.Equal("8.0-1", installed.Version);
        Assert.Equal(reports[^1].Item2, reports[^1].Item1);
        Assert.Equal(installed.Folder, tools.Installed!.Folder);
        Assert.Equal(FfmpegOrigin.Downloaded, tools.Find(null)!.Origin);
        Assert.Equal(installed.Ffprobe, tools.Find(null)!.Ffprobe);
        Assert.False(tools.Offers(manifest));
        // Nothing half-done is left beside it.
        Assert.Equal(new[] { installed.Folder }, Directory.GetFileSystemEntries(tools.Folder));

        // A newer one replaces it; an older one is not offered.
        var (newer, _) = Publish("8.1-1", "2026-12-01T00:00:00Z");
        var next = await Tools(newer).FetchManifestAsync();
        Assert.True(tools.Offers(next));
        await tools.InstallAsync(next);
        Assert.Equal("8.1-1", tools.Installed!.Version);
        Assert.False(Directory.Exists(installed.Folder));
        Assert.False(tools.Offers(manifest));

        // A chosen ffmpeg comes first; one chosen that is gone is none.
        Assert.Equal(FfmpegOrigin.Chosen, tools.Find(tools.Installed.Ffmpeg)!.Origin);
        Assert.Null(tools.Find(Path.Combine(root, "missing", "ffmpeg")));

        tools.Remove();
        Assert.Null(tools.Installed);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchOrDoesNotRunLeavesNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var (manifestUrl, zip) = Publish("8.0-1", "2026-11-20T00:00:00Z");
        var tools = Tools(manifestUrl);
        var manifest = await tools.FetchManifestAsync();
        // The file on the server is not the one signed for.
        var bytes = File.ReadAllBytes(zip);
        bytes[^1] ^= 1;
        File.WriteAllBytes(zip, bytes);
        await Assert.ThrowsAsync<FfmpegToolsException>(() => tools.InstallAsync(manifest));
        Assert.Null(tools.Installed);
        Assert.Empty(Directory.GetFileSystemEntries(tools.Folder));

        // Signed and matching, but not an ffmpeg that runs.
        var (bad, _) = Publish("8.0-2", "2026-11-21T00:00:00Z", say: "something else");
        var badManifest = await Tools(bad).FetchManifestAsync();
        await Assert.ThrowsAsync<FfmpegToolsException>(() => tools.InstallAsync(badManifest));
        Assert.Null(tools.Installed);
        Assert.Empty(Directory.GetFileSystemEntries(tools.Folder));
    }

    [Fact]
    public async Task APlatformWithoutAPackageIsNotOffered()
    {
        var (manifestUrl, _) = Publish("8.0-1", "2026-11-20T00:00:00Z");
        var tools = new FfmpegTools(Path.Combine(root, "data"), manifest: manifestUrl, keys: Keys, rid: null);
        var manifest = await tools.FetchManifestAsync();
        Assert.False(tools.Offers(manifest));
        await Assert.ThrowsAsync<FfmpegToolsException>(() => tools.InstallAsync(manifest));
    }

    // Progress<T> posts to a context; this one reports at once.
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
