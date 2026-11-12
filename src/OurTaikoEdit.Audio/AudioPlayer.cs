using System.Diagnostics;
using Silk.NET.OpenAL;

namespace OurTaikoEdit.Audio;

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
    // A don or ka, started now: heard after the device's latency.
    void PlayHit(bool ka, bool big);
    // A don or ka for a time of the song that is still to come, the song playing: it starts on
    // that very sample of the song, so the two are heard together whatever the device's latency.
    // Asked for a little ahead (the player needs the time before the device has got that far).
    void PlayHitAt(double songTime, bool ka, bool big);
    // The hits asked for ahead that have not started are dropped: the song stopped or moved.
    void CancelHits();
    // Seconds from the device being handed a sample to it being heard, where the system says
    // (0 where it does not).
    double OutputLatency { get; }
    // The don and ka sounds to play from now on.
    void SetHitSounds(PcmAudio don, PcmAudio ka);
}

// OpenAL Soft (bundled for Windows, macOS and Linux): the whole song in one buffer, so seeking and
// the position are exact; two more sources for synthesised don and ka sounds.
public sealed unsafe class OpenAlPlayer : IAudioPlayer
{
    readonly ALContext alc;
    readonly AL al;
    readonly Device* device;
    readonly Context* context;
    readonly uint buffer, source;
    uint donBuffer, kaBuffer;
    readonly uint[] hitSources;
    // The time of the song each hit source was asked to start at; -∞ for one started at once.
    readonly double[] hitTimes;
    int nextHit;
    // OpenAL Soft's AL_SOFT_source_start_delay and ALC_SOFT_device_clock: a source started at a
    // time of the device's clock, and where the song is at a time of that clock. Null without them.
    readonly delegate* unmanaged[Cdecl]<uint, long, void> playAtTime;
    readonly delegate* unmanaged[Cdecl]<uint, int, double*, void> getSourceDoubles;
    const int SecOffsetClock = 0x1203;
    // Without them: hits waiting for their time, started from Position as the song gets there.
    readonly List<(double Time, bool Ka, bool Big)> waiting = new();
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
        hitTimes = Enumerable.Repeat(double.NegativeInfinity, hitSources.Length).ToArray();
        if (al.IsExtensionPresent("AL_SOFT_source_start_delay") && al.IsExtensionPresent("AL_SOFT_source_latency"))
        {
            playAtTime = (delegate* unmanaged[Cdecl]<uint, long, void>)al.GetProcAddress("alSourcePlayAtTimeSOFT");
            getSourceDoubles = (delegate* unmanaged[Cdecl]<uint, int, double*, void>)al.GetProcAddress("alGetSourcedvSOFT");
            if (getSourceDoubles == null) playAtTime = null;
        }
    }

    public double OutputLatency => OutputDevice.Latency;

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
            StartWaiting(reported);
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
        CancelHits();
        al.SourcePause(source);
        lastReport = -1;
    }

    public void Seek(double seconds)
    {
        CancelHits();
        seekTo = Math.Clamp(seconds, 0, Duration);
        al.SetSourceProperty(source, SourceFloat.SecOffset, (float)seekTo);
        lastReport = IsPlaying ? seekTo : -1;
        sinceReport.Restart();
    }

    public void SetHitSounds(PcmAudio don, PcmAudio ka)
    {
        foreach (uint s in hitSources) { al.SourceStop(s); al.SetSourceProperty(s, SourceInteger.Buffer, 0); }
        al.DeleteBuffer(donBuffer); al.DeleteBuffer(kaBuffer);
        donBuffer = Upload(don);
        kaBuffer = Upload(ka);
    }

    // The next hit source, ready to play a don or a ka.
    (uint Source, int Index) Ready(bool ka, bool big)
    {
        int i = nextHit++ % hitSources.Length;
        uint s = hitSources[i];
        al.SourceStop(s);
        al.SetSourceProperty(s, SourceInteger.Buffer, ka ? kaBuffer : donBuffer);
        al.SetSourceProperty(s, SourceFloat.Gain, (big ? 1f : 0.7f) * Math.Clamp(HitVolume, 0, 1));
        return (s, i);
    }

    public void PlayHit(bool ka, bool big)
    {
        var (s, i) = Ready(ka, big);
        hitTimes[i] = double.NegativeInfinity;
        al.SourcePlay(s);
    }

    // The song's place at a time of the device's clock gives the clock's time for a place of the
    // song; the source is started then, to the sample. A source played with alSourcePlay instead
    // starts with the next block the device mixes (11 ms blocks on macOS), where the song is by
    // then, and only once a timer has seen that the time has come.
    public void PlayHitAt(double songTime, bool ka, bool big)
    {
        if (!IsPlaying) { PlayHit(ka, big); return; }
        if (playAtTime == null) { waiting.Add((songTime, ka, big)); return; }
        var (s, i) = Ready(ka, big);
        double* at = stackalloc double[2];
        getSourceDoubles(source, SecOffsetClock, at);
        hitTimes[i] = songTime;
        playAtTime(s, (long)Math.Round((at[1] + (songTime - at[0])) * 1e9));
    }

    // Without the extensions: a hit is started when the block the device mixes next is the one
    // nearest its time (the reported place is where that block starts).
    void StartWaiting(double reported)
    {
        if (waiting.Count == 0) return;
        const double halfBlock = 0.005;
        for (int i = 0; i < waiting.Count; i++)
        {
            if (waiting[i].Time > reported + halfBlock) continue;
            PlayHit(waiting[i].Ka, waiting[i].Big);
            waiting.RemoveAt(i--);
        }
    }

    public void CancelHits()
    {
        waiting.Clear();
        al.GetSourceProperty(source, SourceFloat.SecOffset, out float reported);
        for (int i = 0; i < hitSources.Length; i++)
        {
            // One that has started rings on; one still waiting for its time is not played.
            if (hitTimes[i] > reported) al.SourceStop(hitSources[i]);
            hitTimes[i] = double.NegativeInfinity;
        }
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
