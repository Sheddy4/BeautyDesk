using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BeautyDesk.Services;

/// <summary>
/// История буфера обмена: последние скопированные тексты. Хранится только в памяти, на диск не попадает.
/// Слушает буфер, только пока на столе есть виджет истории.
/// То, что менеджеры паролей помечают как «не сохранять в истории», пропускается.
/// </summary>
internal sealed class ClipboardHistory : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D, Limit = 20, MaxLength = 4000;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly List<string> _items = [];
    private HwndSource? _window;
    private string? _ignoreNext; // текст, который положили в буфер мы сами (клик по записи)

    public IReadOnlyList<string> Items => _items;
    public event Action? Changed;

    public void SetActive(bool active)
    {
        if (active == (_window is not null)) return;
        if (active)
        {
            _window = new HwndSource(new HwndSourceParameters("BeautyDesk.Clipboard") { Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(WndProc);
            AddClipboardFormatListener(_window.Handle);
        }
        else
        {
            RemoveClipboardFormatListener(_window!.Handle);
            _window.Dispose();
            _window = null;
            _items.Clear(); // перестали слушать — история не хранится
            Changed?.Invoke();
        }
    }

    /// <summary>Кладёт запись обратно в буфер; она поднимается наверх списка.</summary>
    public void Copy(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var text = _items[index];
        _items.RemoveAt(index);
        _items.Insert(0, text);
        _ignoreNext = text;
        SetText(text);
        Changed?.Invoke();
    }

    public static void SetText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (COMException) { Thread.Sleep(40); } // буфер занят другой программой
        }
    }

    public void Clear()
    {
        _items.Clear();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_window is not null) SetActive(false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Read();
        return IntPtr.Zero;
    }

    private void Read()
    {
        string? text = null;
        for (int attempt = 0; attempt < 5 && text is null; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null) return;
                // Менеджеры паролей и Windows помечают секреты — такое не запоминаем.
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream flag
                    && flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0) return;
                if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
                text = data.GetData(DataFormats.UnicodeText) as string ?? "";
            }
            catch (COMException) { Thread.Sleep(40); }
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxLength) text = text[..MaxLength];
        if (text == _ignoreNext) { _ignoreNext = null; return; }
        _items.Remove(text);
        _items.Insert(0, text);
        if (_items.Count > Limit) _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke();
    }
}
