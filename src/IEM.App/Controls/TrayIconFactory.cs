using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IEM.Core.Presentation;

namespace IEM.App.Controls;

/// <summary>
/// Draws the tray icon at runtime, coloured by the current verdict.
/// <para>
/// Drawn rather than shipped as image files for a reason beyond convenience: the icon has
/// to change as the verdict changes, and a set of pre-rendered files would have to be kept
/// in step with the palette by hand. Generating it from the same colours the charts use
/// means the tray, the window and the report can never disagree about what red means.
/// </para>
/// <para>
/// Produces a real <see cref="Icon"/> rather than a WPF image. A tray icon is a native
/// icon handle at the Win32 level, and the tray component can only convert an image it can
/// resolve from a URI - which a bitmap generated in memory has no way of providing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIconFactory
{
    private const int Size = 32;

    // Classic DllImport rather than the source-generated form, which would require the
    // whole project to allow unsafe code for one blittable call.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    /// <summary>Builds an icon for the given verdict. The caller owns the result.</summary>
    public static Icon Create(VerdictKind kind, bool running)
    {
        var fill = kind switch
        {
            VerdictKind.UpstreamFault => Color.FromArgb(0xC0, 0x39, 0x2B),
            VerdictKind.LocalFault => Color.FromArgb(0xE0, 0xA8, 0x00),
            VerdictKind.Stable => Color.FromArgb(0x2E, 0x9E, 0x5B),
            _ => Color.FromArgb(0x9A, 0x9A, 0xA2),
        };

        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // Filled while a session runs, a ring while idle. Colour alone would be useless
            // to anyone who cannot separate red from green, and at this size shape is the
            // only other signal there is room for.
            var bounds = new Rectangle(3, 3, Size - 7, Size - 7);

            if (running)
            {
                using var brush = new SolidBrush(fill);
                graphics.FillEllipse(brush, bounds);
            }
            else
            {
                using var pen = new Pen(fill, 4.5f);
                graphics.DrawEllipse(pen, bounds);
            }
        }

        var handle = bitmap.GetHicon();

        try
        {
            // Cloned because the icon from FromHandle does not own its handle. Without
            // releasing it explicitly, every colour change would leak a GDI handle - and
            // this application is expected to run for days.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
