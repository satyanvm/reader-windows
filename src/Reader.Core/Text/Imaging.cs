namespace Reader.Core.Text;

/// <summary>A rendered page: 32-bit BGRA pixels, rows top to bottom, <c>stride = width * 4</c>.</summary>
public sealed record RasterImage(int Width, int Height, byte[] Bgra);

/// <summary>
/// Renders a PDF page to pixels on white (for OCR and figure pages). On Windows this is
/// <c>Windows.Data.Pdf</c> (Reader.App); tests use a fake.
/// </summary>
public interface IPageRasterizer
{
    /// <summary>Page <paramref name="pageIndex"/> of the PDF at <paramref name="pdfPath"/>, <paramref name="width"/> pixels wide; null if it can't be rendered.</summary>
    RasterImage? Render(string pdfPath, int pageIndex, int width);
}

/// <summary>
/// Reads text from a page image, for scanned PDFs. Output lines go through the same
/// <see cref="Reflow"/> as PDF text, with pixel coordinates and the line height standing in for
/// font size, exactly like the Vision path on the Mac and ML Kit on Android. On Windows this is
/// <c>Windows.Media.Ocr</c>, on device.
/// </summary>
public interface IOcr
{
    List<Line> Lines(RasterImage image);
}
