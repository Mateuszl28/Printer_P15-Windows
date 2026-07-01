using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace P15Printer;

/// <summary>
/// Converts images and text into the 1-bit MSB-first raster the P15 expects.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageEncoder
{
    /// <summary>Default printable width of the P15 head in dots (203 dpi, ~48 mm).</summary>
    public const int DefaultWidthDots = 384;

    public readonly record struct Raster(byte[] Data, int WidthBytes, int Height);

    /// <summary>
    /// Loads an image file, scales it to <paramref name="widthDots"/> (preserving
    /// aspect ratio), and packs it into a 1bpp raster.
    /// </summary>
    public static Raster FromFile(string path, int widthDots = DefaultWidthDots, bool dither = true)
    {
        using var src = new Bitmap(path);
        return FromBitmap(src, widthDots, dither);
    }

    public static Raster FromBitmap(Bitmap src, int widthDots = DefaultWidthDots, bool dither = true)
    {
        int height = Math.Max(1, (int)Math.Round(src.Height * (widthDots / (double)src.Width)));

        using var scaled = new Bitmap(widthDots, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, widthDots, height);
        }

        return Pack(ToGray(scaled, widthDots, height), widthDots, height, dither);
    }

    private static double[,] ToGray(Bitmap bmp, int w, int h)
    {
        var gray = new double[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                gray[y, x] = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
            }
        return gray;
    }

    private static Raster Pack(double[,] gray, int w, int h, bool dither)
    {
        int widthBytes = (w + 7) / 8;
        var data = new byte[widthBytes * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = gray[y, x];
                bool black = v < 128;
                if (black) data[y * widthBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));

                if (dither)
                {
                    // Floyd–Steinberg error diffusion.
                    double err = v - (black ? 0 : 255);
                    Spread(gray, x + 1, y,     w, h, err * 7 / 16);
                    Spread(gray, x - 1, y + 1, w, h, err * 3 / 16);
                    Spread(gray, x,     y + 1, w, h, err * 5 / 16);
                    Spread(gray, x + 1, y + 1, w, h, err * 1 / 16);
                }
            }
        }
        return new Raster(data, widthBytes, h);
    }

    private static void Spread(double[,] gray, int x, int y, int w, int h, double err)
    {
        if (x >= 0 && x < w && y >= 0 && y < h) gray[y, x] += err;
    }

    /// <summary>Renders a text block to a raster using the given font.</summary>
    public static Raster FromText(string text, int widthDots = DefaultWidthDots,
                                  string fontName = "Segoe UI", float fontSize = 28f)
    {
        using var font = new Font(fontName, fontSize, GraphicsUnit.Pixel);

        // Measure required height.
        SizeF size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            size = g.MeasureString(text, font, widthDots);

        int height = Math.Max(1, (int)Math.Ceiling(size.Height) + 8);

        using var bmp = new Bitmap(widthDots, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var black = new SolidBrush(Color.Black);
            g.DrawString(text, font, black, new RectangleF(0, 4, widthDots, height));
        }

        return Pack(ToGray(bmp, widthDots, height), widthDots, height, dither: false);
    }
}
