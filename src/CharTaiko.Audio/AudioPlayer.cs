using System.Diagnostics;
using Silk.NET.OpenAL;

namespace CharTaiko.Audio;

// Plays one song and the hit sounds. Positions are seconds of the song.
public interface IAudioPlayer : IDisposable
{
    double Duration { get; }
    bool IsPlaying { get; }
    // Where the song is: between the device's reports it runs on from the last one by the clock.
    double Position { get; }
    float Volume { get; set; }
    // The don and ka sounds, 0 to 1.
    float HitVolume { get; set; }
    void Play();
    void Pause();
    void Seek(double seconds);
    void PlayHit(bool ka, bool big);
}

// OpenAL Soft (bundled for Windows, macOS and Linux): the whole song in one buffer, so seeking and
// the position are exact; two more sources for synthesised don and ka sounds.
public sealed unsafe class OpenAlPlayer : IAudioPlayer
{
    readonly ALContext alc;
    readonly AL al;
    readonly Device* device;
    readonly Context* context;
    readonly uint buffer, source, donBuffer, kaBuffer;
    readonly uint[] hitSources;
    int nextHit;
    readonly Stopwatch sinceReport = new();
    double lastReport = -1;
    double seekTo;

    public double Duration { get; }

    OpenAlPlayer(ALContext alc, AL al, Device* device, Context* context, PcmAudio audio)
    {
        this.alc = alc; this.al = al; this.device = device; this.context = context;
        Duration = audio.Duration;
        buffer = Upload(audio);
        source = al.GenSource();
        al.SetSourceProperty(source, SourceInteger.Buffer, buffer);
        donBuffer = Upload(HitSounds.Don(44100));
        kaBuffer = Upload(HitSounds.Ka(44100));
        hitSources = Enumerable.Range(0, 8).Select(_ => al.GenSource()).ToArray();
    }

    // The player, or null when there is no audio device (the editor then runs a silent clock).
    public static OpenAlPlayer? Open(PcmAudio audio)
    {
        try
        {
            var alc = ALContext.GetApi(true);
            var al = AL.GetApi(true);
            var device = alc.OpenDevice("");
            if (device == null) return null;
            var context = alc.CreateContext(device, null);
            alc.MakeContextCurrent(context);
            return new OpenAlPlayer(alc, al, device, context, audio);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    uint Upload(PcmAudio audio)
    {
        // Mono stays mono, anything else is mixed to stereo.
        int channels = audio.Channels == 1 ? 1 : 2;
        long frames = audio.Frames;
        var pcm = new short[frames * channels];
        for (long f = 0; f < frames; f++)
        {
            if (channels == 1) pcm[f] = ToShort(audio.Samples[f]);
            else
            {
                int ch = audio.Channels;
                float l = audio.Samples[f * ch], r = audio.Samples[f * ch + (ch > 1 ? 1 : 0)];
                pcm[f * 2] = ToShort(l); pcm[f * 2 + 1] = ToShort(r);
            }
        }
        uint id = al.GenBuffer();
        fixed (short* p = pcm)
            al.BufferData(id, channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16, p, pcm.Length * 2, audio.SampleRate);
        return id;

        static short ToShort(float s) => (short)Math.Clamp(s * 32767f, -32768f, 32767f);
    }

    public bool IsPlaying
    {
        get
        {
            al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
            return state == (int)SourceState.Playing;
        }
    }

    public double Position
    {
        get
        {
            if (!IsPlaying) return lastReport >= 0 ? lastReport : seekTo;
            al.GetSourceProperty(source, SourceFloat.SecOffset, out float reported);
            if (reported != lastReport) { lastReport = reported; sinceReport.Restart(); }
            return lastReport + sinceReport.Elapsed.TotalSeconds;
        }
    }

    public float HitVolume { get; set; } = 0.8f;

    public float Volume
    {
        get { al.GetSourceProperty(source, SourceFloat.Gain, out float g); return g; }
        set => al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(value, 0, 1));
    }

    public void Play()
    {
        al.SourcePlay(source);
        al.SetSourceProperty(source, SourceFloat.SecOffset, (float)seekTo);
        lastReport = seekTo;
        sinceReport.Restart();
    }

    public void Pause()
    {
        seekTo = Position;
        al.SourcePause(source);
        lastReport = -1;
    }

    public void Seek(double seconds)
    {
        seekTo = Math.Clamp(seconds, 0, Duration);
        al.SetSourceProperty(source, SourceFloat.SecOffset, (float)seekTo);
        lastReport = IsPlaying ? seekTo : -1;
        sinceReport.Restart();
    }

    public void PlayHit(bool ka, bool big)
    {
        uint s = hitSources[nextHit++ % hitSources.Length];
        al.SourceStop(s);
        al.SetSourceProperty(s, SourceInteger.Buffer, ka ? kaBuffer : donBuffer);
        al.SetSourceProperty(s, SourceFloat.Gain, (big ? 1f : 0.7f) * Math.Clamp(HitVolume, 0, 1));
        al.SourcePlay(s);
    }

    public void Dispose()
    {
        al.SourceStop(source);
        al.DeleteSource(source);
        foreach (uint s in hitSources) { al.SourceStop(s); al.DeleteSource(s); }
        al.DeleteBuffer(buffer); al.DeleteBuffer(donBuffer); al.DeleteBuffer(kaBuffer);
        alc.MakeContextCurrent(null);
        alc.DestroyContext(context);
        alc.CloseDevice(device);
    }
}

// Short don and ka sounds made at start-up, so no sample files are needed.
public static class HitSounds
{
    // A low drum hit: a falling sine with a fast decay.
    public static PcmAudio Don(int rate)
    {
        int n = rate / 6;
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, f = 90 + 140 * Math.Exp(-t * 40);
            phase += 2 * Math.PI * f / rate;
            s[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 22) * 0.9);
        }
        return new PcmAudio { SampleRate = rate, Channels = 1, Samples = s };
    }

    // A rim click: a high tone and a burst of noise, very short.
    public static PcmAudio Ka(int rate)
    {
        int n = rate / 12;
        var s = new float[n];
        var random = new Random(7);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            s[i] = (float)((Math.Sin(2 * Math.PI * 1900 * t) * 0.5 + (random.NextDouble() * 2 - 1) * 0.35) * Math.Exp(-t * 70));
        }
        return new PcmAudio { SampleRate = rate, Channels = 1, Samples = s };
    }
}
