using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PathForge.Core.Machining;

namespace PathForge.App.Services;

/// <summary>Loads PNG/JPG/BMP/GIF/TIFF pictures as grey-scale images for laser engraving.</summary>
public static class ImageLoader
{
    public const string Filter = "Картинки (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Все файлы (*.*)|*.*";

    /// <summary>Longest side kept in the project; larger pictures are scaled down (enough for 0.05 mm lines on 100 mm).</summary>
    private const int MaxSide = 2000;

    public static GrayImage Load(string path)
    {
        BitmapSource source;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }

        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > MaxSide)
        {
            var scale = (double)MaxSide / longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        bgra.CopyPixels(buffer, stride, 0);

        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var b = buffer[i * 4];
            var g = buffer[i * 4 + 1];
            var r = buffer[i * 4 + 2];
            var a = buffer[i * 4 + 3] / 255.0;
            var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
            // Transparent areas count as white (nothing to burn).
            gray[i] = (byte)Math.Round(luminance * a + 255 * (1 - a));
        }

        return new GrayImage { Width = width, Height = height, Pixels = gray, SourceName = Path.GetFileName(path) };
    }
}
