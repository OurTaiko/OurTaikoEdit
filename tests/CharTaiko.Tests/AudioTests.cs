using CharTaiko.Audio;

namespace CharTaiko.Tests;

public class AudioTests
{
    // One second of stereo: silence, then a loud half second.
    static PcmAudio Tone()
    {
        const int rate = 8000;
        var s = new float[rate * 2];
        for (int f = rate / 2; f < rate; f++) s[f * 2] = s[f * 2 + 1] = (float)Math.Sin(2 * Math.PI * 440 * f / rate) * 0.8f;
        return new PcmAudio { SampleRate = rate, Channels = 2, Samples = s };
    }

    [Fact]
    public void AWavReadsBackAsWritten()
    {
        var tone = Tone();
        var read = PcmAudio.Wav(tone.ToWav());
        Assert.Equal((8000, 2, 8000L), (read.SampleRate, read.Channels, read.Frames));
        Assert.Equal(1.0, read.Duration, 6);
        Assert.InRange(read.Samples.Max(), 0.79f, 0.81f);
    }

    [Fact]
    public void TheWaveformIsQuietThenLoud()
    {
        var wave = new Waveform(Tone());
        Assert.Equal(0, wave.Peak(0, 0.45));
        Assert.InRange(wave.Peak(0.6, 0.9), 0.7f, 0.81f);
        Assert.Equal(0, wave.Peak(5, 6));
    }

    [Fact]
    public void NotAWavIsRefused() => Assert.Throws<InvalidDataException>(() => PcmAudio.Wav(new byte[16]));

    [Fact]
    public void HitSoundsAreShortAndQuietAtTheEnd()
    {
        foreach (var hit in new[] { HitSounds.Don(44100), HitSounds.Ka(44100) })
        {
            Assert.InRange(hit.Duration, 0.05, 0.2);
            Assert.True(Math.Abs(hit.Samples[^1]) < 0.05);
        }
    }
}
