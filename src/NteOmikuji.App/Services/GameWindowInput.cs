using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NteOmikuji.Core;
using NteOmikuji.Core.Session;

namespace NteOmikuji.App.Services;

/// <summary>
/// Clicks the history's next-page button by posting messages to the game window. The game also reads the real
/// cursor, so it visits the button for each click and goes straight back. Nothing needs installing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameWindowInput : IPageInput, IDisposable
{
    private const string GameProcess = "HTGame";
    private const uint WmActivate = 0x0006;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const nuint WaActive = 1;
    private const nuint MkLButton = 1;
    private const int VkEscape = 0x1B;
    private const int CursorTolerance = 3;

    /// <summary>The screen these button positions were measured on. The game keeps its dialogs centred at this shape.</summary>
    private const double DesignWidth = 1280;
    private const double DesignHeight = 720;

    private static readonly TimeSpan CursorSettle = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan ButtonHold = TimeSpan.FromMilliseconds(20);

    private readonly (double X, double Y) _button;
    private readonly PhysicalMouseGuard _guard = new();
    private Native.Point? _cursor;

    public bool HoldsPhysicalMouse => _guard.Installed;

    public GameWindowInput(HistoryKind kind)
    {
        _button = kind == HistoryKind.ArcMiracleBox ? (820, 576) : (815, 594);
        // Forgets a press from before paging began, such as the one that switched to the game.
        Native.GetAsyncKeyState(VkEscape);
    }

    public static bool CanPage(HistoryKind kind) => kind is HistoryKind.LimitedCharacter or HistoryKind.Permanent or HistoryKind.ArcMiracleBox;

    public static bool GameIsForeground() => ForegroundGame() != 0;

    public AutoPageOutcome? Blocker()
    {
        if (ForegroundGame() == 0)
        {
            return AutoPageOutcome.LostFocus;
        }
        if (Native.GetAsyncKeyState(VkEscape) != 0)
        {
            return AutoPageOutcome.Interrupted;
        }
        Native.GetCursorPos(out var cursor);
        if (_cursor is { } known && (Math.Abs(known.X - cursor.X) > CursorTolerance || Math.Abs(known.Y - cursor.Y) > CursorTolerance))
        {
            return AutoPageOutcome.Interrupted;
        }
        _cursor = cursor;
        return null;
    }

    public async Task<bool> ClickNextAsync(CancellationToken token)
    {
        var game = ForegroundGame();
        if (game == 0)
        {
            return false;
        }
        // Delivered before the cursor moves, so a game Windows will not let this app reach leaves the cursor alone.
        if (!Native.PostMessage(game, WmActivate, WaActive, 0))
        {
            return false;
        }
        _guard.Holding = true;
        Native.GetCursorPos(out var home);
        Native.GetClientRect(game, out var client);
        var scale = Math.Min(client.Right / DesignWidth, client.Bottom / DesignHeight);
        var x = (int)(((client.Right - (DesignWidth * scale)) / 2) + (_button.X * scale));
        var y = (int)(((client.Bottom - (DesignHeight * scale)) / 2) + (_button.Y * scale));
        var target = new Native.Point { X = x, Y = y };
        Native.ClientToScreen(game, ref target);
        var position = (nint)((y << 16) | (x & 0xFFFF));
        try
        {
            Native.SetCursorPos(target.X, target.Y);
            await Task.Delay(CursorSettle, CancellationToken.None);
            var delivered = Native.PostMessage(game, WmLButtonDown, MkLButton, position);
            await Task.Delay(ButtonHold, CancellationToken.None);
            delivered &= Native.PostMessage(game, WmLButtonUp, 0, position);
            await Task.Delay(CursorSettle, CancellationToken.None);
            return delivered;
        }
        finally
        {
            Native.SetCursorPos(home.X, home.Y);
            _guard.Holding = false;
        }
    }

    public void Dispose() => _guard.Dispose();

    private static nint ForegroundGame()
    {
        var window = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(GameProcess, StringComparison.OrdinalIgnoreCase) ? window : 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X, Y;
        }

        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] public static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    }
}
