using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroRecorder;

public readonly record struct KeyInput(int Vk, int Scan, bool Extended, bool Down, bool FromPlayback);

public readonly record struct MouseInput(int Message, int X, int Y, uint MouseData, bool FromPlayback);

/// <summary>
/// System-wide low-level keyboard and mouse hooks. Callbacks run on the thread that called
/// <see cref="Install"/>, which must pump messages (the WinForms UI thread does).
/// </summary>
public sealed class GlobalHooks : IDisposable
{
    /// <summary>Return true to swallow the key so no other application sees it.</summary>
    public Func<KeyInput, bool>? KeyboardInput;
    public Action<MouseInput>? MouseInput;

    // Delegates must stay referenced for as long as the hooks are installed.
    private readonly Native.LowLevelProc _keyboardProc;
    private readonly Native.LowLevelProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public GlobalHooks()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public void Install()
    {
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && KeyboardInput is { } handler)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            var input = new KeyInput(
                (int)data.vkCode,
                (int)data.scanCode,
                (data.flags & Native.LLKHF_EXTENDED) != 0,
                down,
                data.dwExtraInfo == Native.PlaybackSignature);
            if (handler(input)) return 1;
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && MouseInput is { } handler)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            handler(new MouseInput((int)wParam, data.pt.X, data.pt.Y, data.mouseData,
                data.dwExtraInfo == Native.PlaybackSignature));
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
