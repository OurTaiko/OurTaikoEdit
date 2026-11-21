using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurTaikoEdit.Core.Ffmpeg;

// ffmpeg manifest tool:
//   keygen --private-out <file>         a new key pair: the private half to the file (mode 600), the public half printed
//   sign --version <v> --root <dir> --out <file> [--key-id <id>] [--published <ISO time>]
//                                       <dir>/<rid>/ffmpeg.zip for each platform; the private key from
//                                       OURTAIKOEDIT_TOOLS_SIGNING_KEY
//   verify <manifest> [--location <url>] checks it as the app does, and prints what it offers
const string KeyVariable = "OURTAIKOEDIT_TOOLS_SIGNING_KEY";

try
{
    return args.FirstOrDefault() switch
    {
        "keygen" => Keygen(),
        "sign" => Sign(),
        "verify" => Verify(),
        _ => Usage(),
    };
}
catch (Exception e) when (e is ToolsManifestException or IOException or ArgumentException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: keygen --private-out <file> | sign --version <v> --root <dir> --out <file> [--key-id <id>] [--published <time>] | verify <manifest> [--location <url>]");
    return 2;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Option(name) ?? throw new ArgumentException($"{name} is required");

int Keygen()
{
    string file = Required("--private-out");
    if (File.Exists(file)) throw new IOException($"{file} already exists");
    var (priv, pub) = ToolsManifest.GenerateKeyPair();
    File.WriteAllText(file, priv + "\n");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    Console.WriteLine(pub);
    return 0;
}

int Sign()
{
    string version = Required("--version"), root = Required("--root"), output = Required("--out");
    string keyId = Option("--key-id") ?? ToolsManifest.TrustedKeys.Keys.Last();
    string key = Environment.GetEnvironmentVariable(KeyVariable)?.Trim() is { Length: > 0 } k ? k : throw new ArgumentException($"{KeyVariable} is not set");
    var published = Option("--published") is { } p ? DateTimeOffset.Parse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : DateTimeOffset.UtcNow;
    var platforms = new JsonObject();
    foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
    {
        string zip = Path.Combine(dir, "ffmpeg.zip");
        if (!File.Exists(zip)) continue;
        string rid = Path.GetFileName(dir);
        using var stream = File.OpenRead(zip);
        platforms[rid] = new JsonObject
        {
            ["url"] = $"{version}/{rid}/ffmpeg.zip",
            ["sha256"] = ToolsManifest.Sha256(stream),
            ["size"] = new FileInfo(zip).Length,
        };
    }
    if (platforms.Count == 0) throw new IOException($"no <rid>/ffmpeg.zip under {root}");
    var manifest = new JsonObject
    {
        ["kind"] = ToolsManifest.Kind,
        ["version"] = version,
        ["published"] = published.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["source"] = $"{version}/source/",
        ["platforms"] = platforms,
    };
    ToolsManifest.Sign(manifest, key, keyId);
    File.WriteAllText(output, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    // The same check the app makes, with the public key it knows.
    ToolsManifest.Parse(File.ReadAllText(output), new Uri("https://example.invalid/manifest.json"));
    Console.WriteLine($"Signed {output} ({keyId}): {string.Join(", ", platforms.Select(p => p.Key))}");
    return 0;
}

int Verify()
{
    string file = args.ElementAtOrDefault(1) ?? throw new ArgumentException("verify needs a manifest file");
    var location = new Uri(Option("--location") ?? FfmpegTools.DefaultManifest.ToString());
    var manifest = ToolsManifest.Parse(File.ReadAllText(file), location);
    Console.WriteLine($"{manifest.Version}, published {manifest.Published:u}, source {manifest.Source}");
    foreach (var (rid, package) in manifest.Platforms) Console.WriteLine($"  {rid}: {package.Url} ({package.Size} bytes, {package.Sha256})");
    return 0;
}
