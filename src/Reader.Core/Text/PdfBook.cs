using Reader.Core.Reader;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Reader.Core.Text;

/// <summary>
/// A PDF opened for reading. Page sizes are known up front (crop box with rotation applied), so
/// every page's height is known before layout and a restore can compute the exact offset without
/// rendering anything. Rendering is done by the app (Windows.Data.Pdf); text extraction opens its
/// own copy on the text worker (see <see cref="TextDocument"/>).
/// </summary>
public sealed class PdfBook : IReaderDocument
{
    public string Path { get; }
    public IReadOnlyList<PageSize> PageSizes { get; }
    public string? MetadataTitle { get; }

    public int PageCount => PageSizes.Count;

    public PdfBook(string path)
    {
        Path = path;
        try
        {
            using var doc = PdfDocument.Open(path);
            var sizes = new List<PageSize>(doc.NumberOfPages);
            for (var i = 1; i <= doc.NumberOfPages; i++)
            {
                try { sizes.Add(DisplaySize(doc.GetPage(i))); }
                catch { sizes.Add(new PageSize(612, 792)); }
            }
            PageSizes = sizes;
            var title = doc.Information?.Title?.Trim();
            MetadataTitle = string.IsNullOrEmpty(title) ? null : title;
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ReaderException("This PDF is password-protected");
        }
        catch (ReaderException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ReaderException("Couldn't open this PDF" + (string.IsNullOrEmpty(e.Message) ? "" : $" ({e.Message})"));
        }
        if (PageSizes.Count == 0) throw new ReaderException("This PDF has no pages");
    }

    public void Close() { }

    /// <summary>Crop box size with the page's rotation applied, in points.</summary>
    public static PageSize DisplaySize(UglyToad.PdfPig.Content.Page page)
    {
        var r = page.CropBox.Bounds;
        var w = Math.Max(1, (int)Math.Round(r.Width, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(r.Height, MidpointRounding.AwayFromZero));
        return ((page.Rotation.Value % 180) + 180) % 180 == 0 ? new PageSize(w, h) : new PageSize(h, w);
    }
}
