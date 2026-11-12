using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KidSafe.Agent.Helper;

internal static class NativeMethods
{
    // ---- idle / foreground ----
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    public static string? ForegroundTitle()
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            return GetWindowText(GetForegroundWindow(), sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    public static string? ForegroundApp()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    // ---- low-level keyboard hook ----
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CONTROL = 0x11;
    private const uint LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static IntPtr _hook;
    private static HookProc? _proc;   // keep delegate alive for the lifetime of the hook

    /// <summary>Blocks Win key, Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc while the lock screen is up. (Ctrl+Alt+Del is OS-reserved.)</summary>
    public static void InstallKeyboardBlock()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = (n, w, l) =>
        {
            if (n >= 0 && (w == WM_KEYDOWN || w == WM_SYSKEYDOWN || w == WM_KEYUP || w == WM_SYSKEYUP))
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
                var alt = (k.flags & LLKHF_ALTDOWN) != 0;
                var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (k.vkCode is VK_LWIN or VK_RWIN
                    || (alt && k.vkCode is VK_TAB or VK_ESCAPE or VK_F4)
                    || (ctrl && k.vkCode == VK_ESCAPE))
                    return (IntPtr)1;
            }
            return CallNextHookEx(_hook, n, w, l);
        };
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public static void RemoveKeyboardBlock()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }
}
