using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using OurTaikoEdit.Audio;

namespace OurTaikoEdit.Browser;

// The song and hit sounds through the browser's Web Audio (audio.js): the same as the desktop's
// OpenAL player, every sound started on the audio clock.
sealed partial class WebAudioPlayer : IAudioPlayer
{
    readonly int id;
    float volume = 1;

    WebAudioPlayer(int id, double duration)
    {
        this.id = id;
        Duration = duration;
    }

    public static IAudioPlayer? Open(PcmAudio audio)
    {
        try
        {
            var player = new WebAudioPlayer(OpenJs(Bytes(audio.Samples), audio.Channels, audio.SampleRate), audio.Duration);
            var pops = PopSounds.Load();
            player.Sound("balloon", pops.Balloon);
            player.Sound("kusudama", pops.Kusudama);
            return player;
        }
        catch (JSException) { return null; }
    }

    static Span<byte> Bytes(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan());

    void Sound(string name, PcmAudio? audio)
    {
        if (audio != null) SoundJs(id, name, Bytes(audio.Samples), audio.Channels, audio.SampleRate);
    }

    public double Duration { get; }
    public bool IsPlaying => PlayingJs(id);
    public double Position => PositionJs(id);
    public float HitVolume { get; set; } = 0.8f;
    public double OutputLatency => LatencyJs();

    public float Volume
    {
        get => volume;
        set { volume = Math.Clamp(value, 0, 1); VolumeJs(id, volume); }
    }

    double seekTo;

    public void Play() => PlayJs(id, seekTo);

    public void Pause()
    {
        seekTo = Position;
        PauseJs(id);
    }

    public void Seek(double seconds)
    {
        seekTo = Math.Clamp(seconds, 0, Duration);
        if (!IsPlaying) return;
        PauseJs(id);
        PlayJs(id, seekTo);
    }

    public void SetHitSounds(PcmAudio don, PcmAudio ka)
    {
        Sound("don", don);
        Sound("ka", ka);
    }

    float HitGain(bool big) => (big ? 1f : 0.7f) * Math.Clamp(HitVolume, 0, 1);

    public void PlayHit(bool ka, bool big) => HitJs(id, ka ? "ka" : "don", HitGain(big), -1);
    public void PlayHitAt(double songTime, bool ka, bool big) => HitJs(id, ka ? "ka" : "don", HitGain(big), Math.Max(0, songTime));
    public void PlayPop(bool kusudama) => HitJs(id, kusudama ? "kusudama" : "balloon", HitGain(true), -1);
    public void PlayPopAt(double songTime, bool kusudama) => HitJs(id, kusudama ? "kusudama" : "balloon", HitGain(true), Math.Max(0, songTime));
    public void CancelHits() => CancelJs(id);

    public void Dispose() => CloseJs(id);

    [JSImport("audio.open", "main.js")]
    private static partial int OpenJs([JSMarshalAs<JSType.MemoryView>] Span<byte> samples, int channels, int rate);

    [JSImport("audio.sound", "main.js")]
    private static partial void SoundJs(int id, string name, [JSMarshalAs<JSType.MemoryView>] Span<byte> samples, int channels, int rate);

    [JSImport("audio.close", "main.js")]
    private static partial void CloseJs(int id);

    [JSImport("audio.playing", "main.js")]
    private static partial bool PlayingJs(int id);

    [JSImport("audio.position", "main.js")]
    private static partial double PositionJs(int id);

    [JSImport("audio.volume", "main.js")]
    private static partial void VolumeJs(int id, double volume);

    [JSImport("audio.play", "main.js")]
    private static partial void PlayJs(int id, double at);

    [JSImport("audio.pause", "main.js")]
    private static partial void PauseJs(int id);

    [JSImport("audio.hit", "main.js")]
    private static partial void HitJs(int id, string name, double gain, double songTime);

    [JSImport("audio.cancel", "main.js")]
    private static partial void CancelJs(int id);

    [JSImport("audio.latency", "main.js")]
    private static partial double LatencyJs();
}
