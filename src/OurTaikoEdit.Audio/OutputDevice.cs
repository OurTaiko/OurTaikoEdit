using System.Runtime.InteropServices;

namespace OurTaikoEdit.Audio;

// What the system says about the device sound goes out of.
public static unsafe class OutputDevice
{
    static readonly Lazy<double> latency = new(Read);

    // Seconds from a sample being handed to the default output device to it being heard, where the
    // system says: on macOS, Core Audio's figures for the device (its own latency, its safety
    // offset, its buffer and its stream's latency). 0 elsewhere, and when it cannot be asked: the
    // latency is then the user's to set. (OpenAL Soft has a figure too, but on macOS it is the
    // buffer it asked for, not the one it got: 40 ms where Core Audio says 27.)
    public static double Latency => latency.Value;

    static double Read()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        try
        {
            if (!Get(SystemObject, "dOut", "glob", out uint device) || device == 0) return 0;
            if (!Get(device, "nsrt", "glob", out double rate) || !(rate > 0)) return 0;
            Get(device, "ltnc", "outp", out uint own);
            Get(device, "saft", "outp", out uint safety);
            Get(device, "fsiz", "glob", out uint buffer);
            uint stream = 0;
            var streams = new Address { Selector = Code("stm#"), Scope = Code("outp") };
            uint size = 0;
            if (AudioObjectGetPropertyDataSize(device, &streams, 0, null, &size) == 0 && size >= sizeof(uint))
            {
                uint* ids = stackalloc uint[(int)(size / sizeof(uint))];
                if (AudioObjectGetPropertyData(device, &streams, 0, null, &size, ids) == 0) Get(ids[0], "ltnc", "glob", out stream);
            }
            double seconds = (own + safety + buffer + stream) / rate;
            // A figure past half a second is not a latency anyone plays with: not believed.
            return seconds is > 0 and < 0.5 ? seconds : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    const uint SystemObject = 1;
    const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    [StructLayout(LayoutKind.Sequential)]
    struct Address { public uint Selector, Scope, Element; }

    [DllImport(CoreAudio)]
    static extern int AudioObjectGetPropertyData(uint obj, Address* address, uint qualifierSize, void* qualifier, uint* size, void* data);

    [DllImport(CoreAudio)]
    static extern int AudioObjectGetPropertyDataSize(uint obj, Address* address, uint qualifierSize, void* qualifier, uint* size);

    // A property's four-letter name as Core Audio's number.
    static uint Code(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    static bool Get<T>(uint obj, string selector, string scope, out T value) where T : unmanaged
    {
        var address = new Address { Selector = Code(selector), Scope = Code(scope) };
        T read = default;
        uint size = (uint)sizeof(T);
        bool ok = AudioObjectGetPropertyData(obj, &address, 0, null, &size, &read) == 0;
        value = ok ? read : default;
        return ok;
    }
}
