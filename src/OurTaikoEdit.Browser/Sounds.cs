using OurTaikoEdit.Audio;

namespace OurTaikoEdit.Browser;

// The hit and pop sounds built into this assembly, written out where HitSoundSets and PopSounds
// read them (there is no folder next to the program in a browser).
static class Sounds
{
    const string Root = "/app/sounds";

    public static void Unpack()
    {
        var assembly = typeof(Sounds).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("sounds/", StringComparison.Ordinal)))
        {
            // MSBuild writes RecursiveDir with the system's separator.
            string path = Path.Combine(Root, name["sounds/".Length..].Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var from = assembly.GetManifestResourceStream(name)!;
            using var to = File.Create(path);
            from.CopyTo(to);
        }
        HitSoundSets.Folder = Path.Combine(Root, "HitSounds");
        PopSounds.Folder = Path.Combine(Root, "Effects");
    }
}
