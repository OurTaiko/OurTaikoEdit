using System.Runtime.InteropServices;

namespace OurTaikoEdit.App.Export;

// Work the person asked for and is waiting on (a video being exported), told to macOS so that App
// Nap does not slow it: with the app's windows out of sight, macOS put the thread that draws the
// frames on its slow cores at a low priority, and an export took twice as long or more (1080p60,
// 96 s instead of 45). Elsewhere this does nothing. Disposed when the work ends.
public sealed class BusyActivity : IDisposable
{
    // NSActivityUserInitiated: App Nap off and the system kept awake while it lasts (not the display).
    const ulong UserInitiated = 0x00FFFFFFUL;
    // NSActivityLatencyCritical: also kept on the fast cores. Without it an export still took a
    // quarter longer (1080p60 veryfast: 50 s instead of 38).
    const ulong LatencyCritical = 0xFF00000000UL;

    IntPtr token;

    BusyActivity(IntPtr token) => this.token = token;

    public static BusyActivity Begin(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return new BusyActivity(IntPtr.Zero);
        try
        {
            IntPtr info = Send(Class("NSProcessInfo"), Selector("processInfo"));
            IntPtr text = SendString(Class("NSString"), Selector("stringWithUTF8String:"), reason);
            IntPtr token = SendActivity(info, Selector("beginActivityWithOptions:reason:"), UserInitiated | LatencyCritical, text);
            // Kept until it ends (it comes autoreleased).
            if (token != IntPtr.Zero) Send(token, Selector("retain"));
            return new BusyActivity(token);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new BusyActivity(IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (token == IntPtr.Zero) return;
        IntPtr info = Send(Class("NSProcessInfo"), Selector("processInfo"));
        SendObject(info, Selector("endActivity:"), token);
        Send(token, Selector("release"));
        token = IntPtr.Zero;
    }

    const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    static extern IntPtr Class(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    static extern IntPtr Selector(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern IntPtr SendString(IntPtr receiver, IntPtr selector, string text);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern IntPtr SendObject(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern IntPtr SendActivity(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);
}
