using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace OurTaikoEdit.Core.Ffmpeg;

// The ffmpeg the app downloads, as the signed manifest on the release CDN describes it:
//
//   { "kind": "ourtaikoedit-ffmpeg", "version": "8.0.1-1", "published": "2026-11-20T12:00:00Z",
//     "source": "8.0.1-1/source/",
//     "platforms": { "osx-arm64": { "url": "8.0.1-1/osx-arm64/ffmpeg.zip", "sha256": "…", "size": 123 }, … },
//     "signature": { "algorithm": "Ed25519", "keyId": "…", "value": "<base64>" } }
//
// The signature is RhythmHub's: Ed25519 over the JCS form of the manifest without its
// "signature" member, by a key the app knows (TrustedKeys; never one the manifest names itself).
// URLs are relative to the manifest's own. The workflow .github/workflows/ffmpeg.yml makes it,
// with the tool tools/OurTaikoEdit.FfmpegManifest.
public sealed record ToolsManifest(string Version, DateTimeOffset Published, Uri Source, IReadOnlyDictionary<string, ToolsPackage> Platforms)
{
    public const string Kind = "ourtaikoedit-ffmpeg";
    public const string Algorithm = "Ed25519";
    public const string SignatureProperty = "signature";

    // The public keys whose signatures are trusted, by keyId. The private half is the GitHub
    // secret OURTAIKOEDIT_TOOLS_SIGNING_KEY of OurTaiko/OurTaikoEdit. Keep a retired key here
    // until no published manifest uses it.
    public static IReadOnlyDictionary<string, string> TrustedKeys { get; } = new Dictionary<string, string>
    {
        ["ourtaikoedit-tools-2026"] = "0+p8vbZQiYRsHjdx3Kje2PLG/7M1kRYpxmyRYOLz9pI=",
    };

    public ToolsPackage? For(string? rid) => rid != null && Platforms.TryGetValue(rid, out var package) ? package : null;

    // A manifest as downloaded from `location`: its signature checked against `keys`, then read.
    public static ToolsManifest Parse(string json, Uri location, IReadOnlyDictionary<string, string>? keys = null)
    {
        JsonObject manifest;
        try { manifest = JsonNode.Parse(json) as JsonObject ?? throw new ToolsManifestException("not a JSON object"); }
        catch (System.Text.Json.JsonException e) { throw new ToolsManifestException(e.Message); }
        Verify(manifest, keys ?? TrustedKeys);
        if ((string?)manifest["kind"] != Kind) throw new ToolsManifestException($"not an {Kind} manifest");
        string version = Text(manifest, "version");
        if (!DateTimeOffset.TryParse(Text(manifest, "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
            throw new ToolsManifestException("bad published time");
        var source = Resolve(location, Text(manifest, "source"));
        if (manifest["platforms"] is not JsonObject platforms) throw new ToolsManifestException("no platforms");
        var packages = new Dictionary<string, ToolsPackage>();
        foreach (var (rid, node) in platforms)
        {
            if (node is not JsonObject p) throw new ToolsManifestException($"bad platform {rid}");
            string sha = Text(p, "sha256").ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new ToolsManifestException($"bad sha256 for {rid}");
            long size = p["size"] is JsonValue v && v.TryGetValue<long>(out long s) && s > 0 ? s : throw new ToolsManifestException($"bad size for {rid}");
            packages[rid] = new ToolsPackage(Resolve(location, Text(p, "url")), sha, size);
        }
        return new ToolsManifest(version, published, source, packages);

        static string Text(JsonObject o, string name)
            => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new ToolsManifestException($"no {name}");
    }

    // Only http(s) URLs, relative ones against the manifest's.
    static Uri Resolve(Uri location, string url)
    {
        if (!Uri.TryCreate(location, url, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && !uri.IsFile))
            throw new ToolsManifestException($"bad url {url}");
        return uri;
    }

    public static void Verify(JsonObject manifest, IReadOnlyDictionary<string, string> keys)
    {
        if (manifest[SignatureProperty] is not JsonObject sig) throw new ToolsManifestException("missing signature");
        if ((string?)sig["algorithm"] != Algorithm) throw new ToolsManifestException("unsupported signature algorithm");
        string? keyId = (string?)sig["keyId"];
        if (keyId == null || !keys.TryGetValue(keyId, out string? publicKey)) throw new ToolsManifestException($"unknown key {keyId}");
        byte[] signature, key;
        try { signature = Convert.FromBase64String((string?)sig["value"] ?? ""); key = Convert.FromBase64String(publicKey); }
        catch (FormatException) { throw new ToolsManifestException("malformed base64"); }
        if (signature.Length != 64 || key.Length != 32) throw new ToolsManifestException("wrong key or signature length");
        byte[] payload = Payload(manifest);
        bool valid;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key));
            verifier.BlockUpdate(payload, 0, payload.Length);
            valid = verifier.VerifySignature(signature);
        }
        catch (ArgumentException) { throw new ToolsManifestException($"key {keyId} is not an Ed25519 public key"); }
        if (!valid) throw new ToolsManifestException("signature does not match");
    }

    // Adds (or replaces) the signature (the signing tool's; the app only verifies).
    public static void Sign(JsonObject manifest, string privateKey, string keyId)
    {
        manifest.Remove(SignatureProperty);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)));
        byte[] payload = JsonCanonicalizer.Canonicalize(manifest);
        signer.BlockUpdate(payload, 0, payload.Length);
        manifest[SignatureProperty] = new JsonObject
        {
            ["algorithm"] = Algorithm,
            ["keyId"] = keyId,
            ["value"] = Convert.ToBase64String(signer.GenerateSignature()),
        };
    }

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (Convert.ToBase64String(key.GetEncoded()), Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    }

    static byte[] Payload(JsonObject manifest)
    {
        var unsigned = (JsonObject)manifest.DeepClone();
        unsigned.Remove(SignatureProperty);
        try { return JsonCanonicalizer.Canonicalize(unsigned); }
        catch (FormatException e) { throw new ToolsManifestException(e.Message); }
    }

    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}

public sealed record ToolsPackage(Uri Url, string Sha256, long Size);

public sealed class ToolsManifestException(string message) : Exception($"The ffmpeg manifest was rejected: {message}.");
