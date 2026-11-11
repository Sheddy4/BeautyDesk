using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeautyDesk.Services;

/// <summary>
/// Крупные иконки программ, папок и корзины — те же, что показывает Проводник.
/// Возвращаются как data-URL, готовые для вставки в страницу.
/// </summary>
internal static class IconLoader
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    /// <param name="parsingName">Путь к файлу или папке либо имя оболочки вроде shell:AppsFolder\… и shell:RecycleBinFolder.</param>
    public static string? DataUrl(string parsingName, int size = 128)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ImageFactoryId, out var factory) != 0 || factory is null)
                return null;
            int hr = factory.GetImage(new SIZE { Width = size, Height = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out handle);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || handle == IntPtr.Zero) return null;

            if (GetObject(handle, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.BitsPixel != 32 || bmp.Bits == IntPtr.Zero)
                return null;

            // Оболочка отдаёт картинку с прозрачностью, но стандартное преобразование WPF её теряет — читаем пиксели сами.
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null,
                bmp.Bits, bmp.WidthBytes * bmp.Height, bmp.WidthBytes);
            source.Freeze();
            return Encode(source);
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException or NotSupportedException)
        {
            Log.Write($"Иконка не получена для {parsingName}: {e.Message}");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) Native.DeleteObject(handle);
        }
    }

    public static string? Encode(ImageSource? image)
    {
        if (image is not BitmapSource bitmap) return null;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
