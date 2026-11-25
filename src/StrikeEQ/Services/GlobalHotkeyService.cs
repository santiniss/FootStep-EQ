using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StrikeEQ.Services;

public enum HotkeyAction
{
    ToggleBypass = 9001,
    ToggleScoutMode = 9002,
    PresetSlot1 = 9003,
    PresetSlot2 = 9004,
    PresetSlot3 = 9005,
    PresetSlot4 = 9006
}

/// <summary>
/// Registers Win32 global hotkeys (Ctrl+Alt+B, Ctrl+Alt+N, Ctrl+Alt+1..4)
/// so players can toggle EQ bypass, Scout/Night Mode, or presets mid-match.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly List<int> _registeredIds = new();

    public event Action<HotkeyAction>? HotkeyPressed;
    public int RegisteredCount => _registeredIds.Count;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        TryRegister(HotkeyAction.ToggleBypass, mods, 0x42);    // Ctrl+Alt+B
        TryRegister(HotkeyAction.ToggleScoutMode, mods, 0x4E); // Ctrl+Alt+N
        TryRegister(HotkeyAction.PresetSlot1, mods, 0x31);     // Ctrl+Alt+1
        TryRegister(HotkeyAction.PresetSlot2, mods, 0x32);     // Ctrl+Alt+2
        TryRegister(HotkeyAction.PresetSlot3, mods, 0x33);     // Ctrl+Alt+3
        TryRegister(HotkeyAction.PresetSlot4, mods, 0x34);     // Ctrl+Alt+4
    }

    private void TryRegister(HotkeyAction action, uint modifiers, uint vk)
    {
        int id = (int)action;
        if (RegisterHotKey(_hwnd, id, modifiers, vk))
            _registeredIds.Add(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (Enum.IsDefined(typeof(HotkeyAction), id))
            {
                HotkeyPressed?.Invoke((HotkeyAction)id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (int id in _registeredIds)
                UnregisterHotKey(_hwnd, id);
            _registeredIds.Clear();
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
