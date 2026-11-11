using System.Text;

namespace OurTaikoEdit.Audio;

// Decoded audio: interleaved float samples in [-1, 1].
public sealed class PcmAudio
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required float[] Samples { get; init; }

    public long Frames => Samples.Length / Math.Max(1, Channels);
    public double Duration => (double)Frames / SampleRate;

    // OGG Vorbis (NVorbis), MP3 (NLayer) and WAV (PCM 8/16/24/32-bit, float 32/64), by extension.
    public static PcmAudio Decode(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ogg" or ".oga" => Ogg(path),
        ".mp3" => Mp3(path),
        ".wav" or ".wave" => Wav(File.ReadAllBytes(path)),
        var other => throw new NotSupportedException($"Audio of type {other} is not supported (OGG, MP3 and WAV are)."),
    };

    static PcmAudio Ogg(string path)
    {
        using var reader = new NVorbis.VorbisReader(path);
        return ReadAll(reader.SampleRate, reader.Channels, reader.TotalSamples * reader.Channels, buffer => reader.ReadSamples(buffer, 0, buffer.Length));
    }

    static PcmAudio Mp3(string path)
    {
        using var file = new NLayer.MpegFile(path);
        return ReadAll(file.SampleRate, file.Channels, 0, buffer => file.ReadSamples(buffer, 0, buffer.Length));
    }

    static PcmAudio ReadAll(int rate, int channels, long expected, Func<float[], int> read)
    {
        var all = new List<float>(expected > 0 && expected < int.MaxValue ? (int)expected : 1 << 20);
        var buffer = new float[rate * channels];
        int n;
        while ((n = read(buffer)) > 0) all.AddRange(new ArraySegment<float>(buffer, 0, n));
        return new PcmAudio { SampleRate = rate, Channels = channels, Samples = all.ToArray() };
    }

    public static PcmAudio Wav(byte[] bytes)
    {
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a WAV file.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            string id = Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            int body = at + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(bytes, body);
                channels = BitConverter.ToUInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToUInt16(bytes, body + 14);
                // WAVE_FORMAT_EXTENSIBLE: the real format is the sub-format's first two bytes.
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(bytes, body + 24);
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("WAV data before its format.");
                int length = Math.Min(size, bytes.Length - body), step = bits / 8, count = length / step;
                var samples = new float[count];
                for (int i = 0; i < count; i++)
                {
                    int p = body + i * step;
                    samples[i] = (format, bits) switch
                    {
                        (1, 8) => (bytes[p] - 128) / 128f,
                        (1, 16) => BitConverter.ToInt16(bytes, p) / 32768f,
                        (1, 24) => ((bytes[p] | bytes[p + 1] << 8 | (sbyte)bytes[p + 2] << 16)) / 8388608f,
                        (1, 32) => BitConverter.ToInt32(bytes, p) / 2147483648f,
                        (3, 32) => BitConverter.ToSingle(bytes, p),
                        (3, 64) => (float)BitConverter.ToDouble(bytes, p),
                        _ => throw new NotSupportedException($"WAV format {format} with {bits} bits is not supported."),
                    };
                }
                return new PcmAudio { SampleRate = rate, Channels = channels, Samples = samples };
            }
            at = body + size + (size & 1);
        }
        throw new InvalidDataException("The WAV file has no data.");
    }

    // A 16-bit PCM WAV of this audio (tests and fixtures).
    public byte[] ToWav()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int data = Samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)Channels);
        w.Write(SampleRate); w.Write(SampleRate * Channels * 2); w.Write((short)(Channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (float s in Samples) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
        return ms.ToArray();
    }
}

// The audio's loudness for drawing: the largest absolute sample of each bucket of frames, all
// channels together.
public sealed class Waveform
{
    public double SecondsPerBucket { get; }
    public float[] Peaks { get; }

    public Waveform(PcmAudio audio, double secondsPerBucket = 0.005)
    {
        SecondsPerBucket = secondsPerBucket;
        int frames = (int)audio.Frames, per = Math.Max(1, (int)(audio.SampleRate * secondsPerBucket)), ch = Math.Max(1, audio.Channels);
        Peaks = new float[(frames + per - 1) / per];
        for (int b = 0; b < Peaks.Length; b++)
        {
            float peak = 0;
            int end = Math.Min(frames, (b + 1) * per);
            for (int f = b * per; f < end; f++)
                for (int c = 0; c < ch; c++) peak = Math.Max(peak, Math.Abs(audio.Samples[f * ch + c]));
            Peaks[b] = peak;
        }
    }

    // The loudest peak between two times (seconds), 0 outside the audio.
    public float Peak(double from, double to)
    {
        int a = Math.Max(0, (int)(from / SecondsPerBucket)), b = Math.Min(Peaks.Length - 1, (int)(to / SecondsPerBucket));
        float peak = 0;
        for (int i = a; i <= b; i++) peak = Math.Max(peak, Peaks[i]);
        return peak;
    }
}
