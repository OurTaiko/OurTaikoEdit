namespace OurTaikoEdit.Audio;

// The game's hit sounds (OurTaikoPlay Assets/OurTaiko/Audio/hit_sounds, see HitSounds/PROVENANCE.md):
// sets numbered from 0 (太鼓, the game's default), each a don.ogg and a ka.ogg, named in
// neiro_list.txt. They are copied next to the program.
public static class HitSoundSets
{
    public static string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "HitSounds");

    // The sets' names, in their number order (empty when the sounds are missing).
    public static IReadOnlyList<string> Names
    {
        get
        {
            string list = Path.Combine(Folder, "neiro_list.txt");
            if (!File.Exists(list)) return Array.Empty<string>();
            return File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
    }

    // A set's don and ka, or the synthesised ones when it cannot be read.
    public static (PcmAudio Don, PcmAudio Ka) Load(int set)
    {
        string folder = Path.Combine(Folder, Math.Max(0, set).ToString());
        try
        {
            return (PcmAudio.Decode(Path.Combine(folder, "don.ogg")), PcmAudio.Decode(Path.Combine(folder, "ka.ogg")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return (HitSounds.Don(44100), HitSounds.Ka(44100));
        }
    }
}
