using System.Runtime.InteropServices.WindowsRuntime;
using Reader.Core.Text;
using WinOcr = global::Windows.Media.Ocr;
using WinImaging = global::Windows.Graphics.Imaging;

namespace Reader.App.Platform;

/// <summary>
/// On-device text recognition with Windows.Media.Ocr (no network). Each recognized line becomes a
/// <see cref="Line"/> in pixel coordinates with its height standing in for the font size, the same
/// as the Vision path on the Mac and ML Kit on Android, so it goes through the same Reflow.
/// </summary>
internal sealed class WindowsOcr : IOcr
{
    private readonly WinOcr.OcrEngine? _engine =
        WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
        ?? WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));

    public bool Available => _engine is not null;

    public List<Line> Lines(RasterImage image)
    {
        if (_engine is null) return new();
        return Task.Run(async () =>
        {
            using var bitmap = WinImaging.SoftwareBitmap.CreateCopyFromBuffer(
                image.Bgra.AsBuffer(), WinImaging.BitmapPixelFormat.Bgra8, image.Width, image.Height, WinImaging.BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(bitmap);
            var lines = new List<Line>();
            foreach (var line in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var w in line.Words)
                {
                    minX = Math.Min(minX, w.BoundingRect.X);
                    minY = Math.Min(minY, w.BoundingRect.Y);
                    maxX = Math.Max(maxX, w.BoundingRect.X + w.BoundingRect.Width);
                    maxY = Math.Max(maxY, w.BoundingRect.Y + w.BoundingRect.Height);
                }
                lines.Add(new Line([new Span(line.Text)], (float)minX, (float)maxX, (float)maxY, (float)(maxY - minY)));
            }
            return lines;
        }).GetAwaiter().GetResult();
    }
}
