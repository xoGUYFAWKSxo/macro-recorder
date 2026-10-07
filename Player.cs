using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroRecorder;

/// <summary>Replays a macro on a background thread using SendInput.</summary>
public sealed class Player
{
    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();

    private CancellationTokenSource? _cts;

    public bool IsPlaying => _cts != null;

    // Read by the UI's refresh timer.
    public volatile int CurrentLoop;
    public volatile int CurrentIndex;

    /// <summary>Raised on the playback thread when playback ends (finished or stopped).</summary>
    public event Action? Finished;

    /// <param name="repeats">Number of times to play; 0 = loop until stopped.</param>
    public void Start(IReadOnlyList<MacroEvent> events, double speed, int repeats)
    {
        if (IsPlaying) return;
        var snapshot = events.ToArray();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Run(snapshot, speed, repeats, token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Macro playback",
        };
        thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    private void Run(MacroEvent[] events, double speed, int repeats, CancellationToken token)
    {
        var heldKeys = new HashSet<(int Vk, int Scan, bool Ext)>();
        var heldButtons = new HashSet<MouseButton>();
        Native.timeBeginPeriod(1);
        try
        {
            for (int loop = 0; repeats == 0 || loop < repeats; loop++)
            {
                CurrentLoop = loop + 1;
                var clock = Stopwatch.StartNew();
                double target = 0;
                for (int i = 0; i < events.Length; i++)
                {
                    var e = events[i];
                    target += e.DelayMs / speed;
                    if (!WaitUntil(clock, target, token)) return;
                    CurrentIndex = i + 1;
                    Send(e);

                    switch (e.Kind)
                    {
                        case EventKind.KeyDown: heldKeys.Add((e.VirtualKey, e.ScanCode, e.Extended)); break;
                        case EventKind.KeyUp: heldKeys.Remove((e.VirtualKey, e.ScanCode, e.Extended)); break;
                        case EventKind.MouseDown: heldButtons.Add(e.Button); break;
                        case EventKind.MouseUp: heldButtons.Remove(e.Button); break;
                    }
                }
                if (events.Length == 0) break;
            }
        }
        finally
        {
            // Never leave a key or button stuck down if playback is stopped mid-macro.
            foreach (var (vk, scan, ext) in heldKeys)
                Send(new MacroEvent { Kind = EventKind.KeyUp, VirtualKey = vk, ScanCode = scan, Extended = ext });
            foreach (var button in heldButtons)
                SendMouse(ButtonFlags(button, down: false), 0, null);

            Native.timeEndPeriod(1);
            _cts = null;
            Finished?.Invoke();
        }
    }

    /// <summary>Sleeps most of the way, then spins for the last ~2 ms for accurate timing.</summary>
    private static bool WaitUntil(Stopwatch clock, double targetMs, CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested) return false;
            double remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return true;
            if (remaining > 2)
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining - 1.5))) return false;
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    private static void Send(MacroEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.MouseMove:
                SendMouse(0, 0, (e.X, e.Y));
                break;
            case EventKind.MouseDown:
            case EventKind.MouseUp:
                bool down = e.Kind == EventKind.MouseDown;
                uint data = e.Button switch { MouseButton.X1 => 1u, MouseButton.X2 => 2u, _ => 0u };
                SendMouse(ButtonFlags(e.Button, down), data, (e.X, e.Y));
                break;
            case EventKind.MouseWheel:
                SendMouse(Native.MOUSEEVENTF_WHEEL, unchecked((uint)e.WheelDelta), (e.X, e.Y));
                break;
            case EventKind.MouseHWheel:
                SendMouse(Native.MOUSEEVENTF_HWHEEL, unchecked((uint)e.WheelDelta), (e.X, e.Y));
                break;
            case EventKind.KeyDown:
            case EventKind.KeyUp:
                SendKey(e);
                break;
        }
    }

    private static uint ButtonFlags(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP,
        MouseButton.Right => down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP,
        MouseButton.Middle => down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP,
        MouseButton.X1 or MouseButton.X2 => down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP,
        _ => 0,
    };

    /// <param name="position">Screen position in physical pixels, or null to act at the current cursor position.</param>
    private static void SendMouse(uint flags, uint mouseData, (int X, int Y)? position)
    {
        int dx = 0, dy = 0;
        if (position is var (x, y))
        {
            // Absolute coordinates are normalised to 0..65536 across the whole virtual desktop. Aiming at the
            // pixel's centre (+0.5) makes the mapping land exactly on it; the naive x*65535/(w-1) drifts by
            // a few pixels on wide multi-monitor desktops.
            int left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int width = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            int height = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            dx = (int)((x - left + 0.5) * 65536.0 / width);
            dy = (int)((y - top + 0.5) * 65536.0 / height);
            // Move and button go in the same input so the click can't land before the move.
            flags |= Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        }
        SendMouseInput(flags, mouseData, dx, dy);
    }

    private static void SendMouseInput(uint flags, uint mouseData, int dx, int dy)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    dwExtraInfo = Native.PlaybackSignature,
                },
            },
        };
        Native.SendInput(1, new[] { input }, InputSize);
    }

    /// <summary>
    /// Taps an unassigned virtual key. After swallowing an Alt+key hotkey this stops the foreground app
    /// from seeing a lone Alt press/release, which would otherwise open its menu bar.
    /// </summary>
    public static void SendMenuMask()
    {
        const int VK_NONE = 0xE8; // unassigned
        SendKey(new MacroEvent { Kind = EventKind.KeyDown, VirtualKey = VK_NONE });
        SendKey(new MacroEvent { Kind = EventKind.KeyUp, VirtualKey = VK_NONE });
    }

    private static void SendKey(MacroEvent e)
    {
        uint flags = e.Kind == EventKind.KeyUp ? Native.KEYEVENTF_KEYUP : 0;
        if (e.Extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        // Prefer scan codes (games and DirectInput apps often ignore virtual-key-only input).
        if (e.ScanCode != 0) flags |= Native.KEYEVENTF_SCANCODE;

        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)e.VirtualKey,
                    wScan = (ushort)e.ScanCode,
                    dwFlags = flags,
                    dwExtraInfo = Native.PlaybackSignature,
                },
            },
        };
        Native.SendInput(1, new[] { input }, InputSize);
    }
}
