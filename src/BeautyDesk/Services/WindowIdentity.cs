using System.IO;
using System.Runtime.InteropServices;

namespace BeautyDesk.Services;

/// <summary>
/// Некоторые программы показывают окна из вспомогательного процесса (окно Steam принадлежит
/// steamwebhelper.exe). Чтобы панель задач знала, что это за программа и как её запустить,
/// такие окна несут «паспорт»: имя и команду запуска. Док читает его так же, как Windows.
/// </summary>
internal static class WindowIdentity
{
    /// <param name="Path">Программа, которую нужно запускать вместо процесса окна; null — паспорта нет.</param>
    /// <param name="Name">Название для показа; null — не задано.</param>
    public sealed record Info(string? Path, string? Name);

    private static readonly Guid RelaunchProperties = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint RelaunchCommand = 2, RelaunchDisplayName = 4;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort Type; public ushort R1, R2, R3; public IntPtr Value; public IntPtr Value2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid id, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    public static Info Read(IntPtr hwnd)
    {
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, typeof(IPropertyStore).GUID, out var store) != 0 || store is null)
                return new Info(null, null);
            try
            {
                var name = Text(store, RelaunchDisplayName);
                // Имя вида «@файл,-номер» — ссылка на ресурс, а не готовый текст.
                if (name is not null && name.StartsWith('@')) name = null;
                return new Info(ExecutablePath(Text(store, RelaunchCommand)), name);
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return new Info(null, null);
        }
    }

    private static string? Text(IPropertyStore store, uint propertyId)
    {
        var key = new PROPERTYKEY { FormatId = RelaunchProperties, PropertyId = propertyId };
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Value) : null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Достаёт путь к программе из команды запуска, в которой могут быть кавычки и параметры.</summary>
    private static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        string candidate;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            candidate = end > 0 ? command[1..end] : command.Trim('"');
        }
        else
        {
            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? command[..(exe + 4)] : command;
        }
        return File.Exists(candidate) ? candidate : null;
    }
}
