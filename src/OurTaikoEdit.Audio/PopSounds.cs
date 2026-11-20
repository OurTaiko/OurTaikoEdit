namespace OurTaikoEdit.Audio;

// The game's sounds of a balloon popping and a kusudama breaking, next to the program (Effects/).
public static class PopSounds
{
    public static string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Effects");

    // Each sound, or null when it cannot be read (nothing is then heard for it).
    public static (PcmAudio? Balloon, PcmAudio? Kusudama) Load() => (Read("balloon_pop.ogg"), Read("kusudama_pop.ogg"));

    static PcmAudio? Read(string name)
    {
        try
        {
            return PcmAudio.Decode(Path.Combine(Folder, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
