using System.Runtime.InteropServices;
using System.Windows.Input;
using AV.Playout.Interop;

namespace AV.Playout.Triggers;

/// <summary>
/// System-wide low-level keyboard hook (WH_KEYBOARD_LL). Sees every key press even
/// when AV Playout is not the active window, so a USB macro keypad fires cues while
/// the operator is in another app. Keys the handler claims are swallowed and never
/// reach the focused application.
///
/// Must be created on the UI thread: Windows calls the hook on the installing
/// thread's message loop, and it must return within ~300 ms or Windows silently
/// drops the hook. The handler should only decide and queue work.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;   // field: keep the delegate alive
    private readonly HashSet<uint> _down = [];
    private readonly HashSet<uint> _swallowed = [];
    private IntPtr _hook;

    public GlobalKeyboardHook()
    {
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Called once per physical press (auto-repeat filtered). Return true to swallow the key.</summary>
    public Func<Hotkey, bool>? KeyPressed { get; set; }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var message = (int)wParam;

            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (_down.Add(info.vkCode))
                {
                    var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);
                    if (!Hotkey.IsModifierKey(key) && KeyPressed?.Invoke(new Hotkey(key, CurrentModifiers())) == true)
                        _swallowed.Add(info.vkCode);
                }
                if (_swallowed.Contains(info.vkCode))
                    return 1;
            }
            else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                _down.Remove(info.vkCode);
                if (_swallowed.Remove(info.vkCode))
                    return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var mods = ModifierKeys.None;
        if (IsDown(NativeMethods.VK_CONTROL)) mods |= ModifierKeys.Control;
        if (IsDown(NativeMethods.VK_SHIFT)) mods |= ModifierKeys.Shift;
        if (IsDown(NativeMethods.VK_MENU)) mods |= ModifierKeys.Alt;
        return mods;

        static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
