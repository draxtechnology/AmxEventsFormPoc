using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AmxEventsFormPoc;

// Same minimize-blocking technique as the AmxShimPoc / AmxNativePoc / AmxWinFormsPoc
// proof-of-concept projects, ported over here so the real operator console can't be
// minimized or hidden either:
//
//   1. MinimizeBox = false (set in MainForm.Designer.cs) removes the button
//      cosmetically.
//   2. WndProc below intercepts WM_SYSCOMMAND / SC_MINIMIZE — covers the taskbar
//      icon, the title bar, and Alt+Space -> Minimize.
//   3. A low-level keyboard hook swallows Win+D ("show desktop") and Win+M
//      ("minimize all windows") before Explorer's own hotkey handling ever sees
//      them. Those two act directly on every top-level window rather than going
//      through WM_SYSCOMMAND, so intercepting that message alone can't catch them.
//
// Kept in its own partial-class file so this plumbing doesn't clutter MainForm.cs's
// actual event-list logic.
public partial class MainForm
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MASK = 0xFFF0; // low nibble of wParam can hold extra bits; mask them off

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_D = 0x44;
    private const int VK_M = 0x4D;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Keeping a field reference to the delegate stops the GC from collecting it
    // while the unmanaged hook still holds a pointer to it.
    private LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;

    private void InstallLowLevelKeyboardHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
    }

    private void UninstallLowLevelKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt64() == WM_KEYDOWN || wParam.ToInt64() == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (winDown && (data.vkCode == VK_D || data.vkCode == VK_M))
            {
                // Non-zero, non-CallNextHookEx return: swallow the key combo here so
                // Explorer's Show-Desktop / Minimize-all hotkey handler never sees it.
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND)
        {
            int command = (int)((long)m.WParam & SC_MASK);
            if (command == SC_MINIMIZE)
            {
                // Swallow it here: skip base.WndProc entirely, so the default
                // minimize handling never runs.
                return;
            }
        }

        base.WndProc(ref m);
    }
}
