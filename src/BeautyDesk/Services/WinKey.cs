using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BeautyDesk.Services;

/// <summary>
/// Следит за клавишей Win. Одиночное нажатие (нажал и отпустил, ничего больше не трогая) сообщается
/// событием, а меню «Пуск» при этом не открывается. Сочетания вроде Win+E и Win+D не затрагиваются.
/// Перехват живёт, пока жив объект: после выхода из программы Win снова открывает «Пуск».
/// </summary>
internal sealed class WinKey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const ushort MaskKey = 0xE8; // код, не закреплённый ни за одной клавишей
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    private static readonly IntPtr Marker = new(0x42444B); // метка наших собственных нажатий

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse; // задаёт размер объединения
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion Data; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private readonly HookProc _proc;
    private readonly IntPtr _hook;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private bool _winDown;
    private bool _usedInCombination;

    /// <summary>Клавишу Win нажали и отпустили саму по себе.</summary>
    public event Action? Pressed;

    public WinKey()
    {
        _proc = OnKey;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("Перехват клавиши Win не установлен");
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (key.ExtraInfo != Marker)
            {
                int message = wParam.ToInt32();
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN, up = message is WM_KEYUP or WM_SYSKEYUP;
                if (key.VkCode is VK_LWIN or VK_RWIN)
                {
                    if (down && !_winDown)
                    {
                        _winDown = true;
                        _usedInCombination = false;
                    }
                    else if (up && _winDown)
                    {
                        _winDown = false;
                        if (!_usedInCombination)
                        {
                            // Windows открывает «Пуск», когда Win отпустили, не нажав ничего другого.
                            // Подставляем нажатие несуществующей клавиши — для системы это уже сочетание.
                            SendMaskKey();
                            _ui.BeginInvoke(() => Pressed?.Invoke());
                        }
                    }
                }
                else if (down && _winDown)
                {
                    _usedInCombination = true;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public const ushort VK_CONTROL = 0x11, VK_TAB = 0x09, VK_D = 0x44, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_WIN = 0x5B;

    /// <summary>
    /// Нажимает сочетание клавиш (например, Win+D) от имени программы. Нажатия помечены, поэтому
    /// собственный перехват клавиши Win их не трогает и лаунчер не открывается.
    /// </summary>
    public static void SendCombo(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].Type = INPUT_KEYBOARD;
            inputs[i].Data.Keyboard = new KEYBDINPUT { Vk = keys[i], ExtraInfo = Marker };
            int up = keys.Length * 2 - 1 - i;
            inputs[up].Type = INPUT_KEYBOARD;
            inputs[up].Data.Keyboard = new KEYBDINPUT { Vk = keys[i], Flags = KEYEVENTF_KEYUP, ExtraInfo = Marker };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMaskKey()
    {
        var inputs = new INPUT[2];
        inputs[0].Type = inputs[1].Type = INPUT_KEYBOARD;
        inputs[0].Data.Keyboard = new KEYBDINPUT { Vk = MaskKey, ExtraInfo = Marker };
        inputs[1].Data.Keyboard = new KEYBDINPUT { Vk = MaskKey, Flags = KEYEVENTF_KEYUP, ExtraInfo = Marker };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }
}
