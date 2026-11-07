using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NteOmikuji.App.Services;

/// <summary>
/// Holds back the physical mouse while the cursor visits a button, so a bump cannot turn a click into a different
/// one. Events this app causes pass, and so do button releases, so a button held down is never left stuck.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PhysicalMouseGuard : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private const int FlagsOffset = 12;
    private const int LlmhfInjected = 1;
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopWait = TimeSpan.FromMilliseconds(500);

    private readonly Native.HookProc _callback;
    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _thread;
    private uint _threadId;
    private volatile bool _holding;

    public PhysicalMouseGuard()
    {
        _callback = OnMouse;
        _thread = new Thread(Pump) { IsBackground = true, Name = "PhysicalMouseGuard" };
        _thread.Start();
        _started.Wait(StartWait);
    }

    /// <summary>False when Windows refused the hook. Paging still works, just without this protection.</summary>
    public bool Installed { get; private set; }

    public bool Holding
    {
        set => _holding = value;
    }

    public void Dispose()
    {
        _holding = false;
        if (_threadId != 0)
        {
            Native.PostThreadMessage(_threadId, WmQuit, 0, 0);
            _thread.Join(StopWait);
        }
        _started.Dispose();
    }

    private void Pump()
    {
        _threadId = Native.GetCurrentThreadId();
        var hook = Native.SetWindowsHookEx(WhMouseLl, _callback, Native.GetModuleHandle(null), 0);
        Installed = hook != 0;
        _started.Set();
        if (hook == 0)
        {
            return;
        }
        // Windows delivers the hook through this thread's messages.
        while (Native.GetMessage(out _, 0, 0, 0) > 0)
        {
        }
        Native.UnhookWindowsHookEx(hook);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && _holding && !IsButtonRelease(message) && (Marshal.ReadInt32(data, FlagsOffset) & LlmhfInjected) == 0)
        {
            return 1;
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private static bool IsButtonRelease(nint message) => message is 0x0202 or 0x0205 or 0x0208 or 0x020C;

    private static class Native
    {
        public delegate nint HookProc(int code, nint message, nint data);

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public nint Window;
            public uint Id;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X, Y;
        }

        [DllImport("user32.dll")] public static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("user32.dll")] public static extern int GetMessage(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    }
}
