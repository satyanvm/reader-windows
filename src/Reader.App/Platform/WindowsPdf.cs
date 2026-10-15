using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reader.Core.Text;
using WinPdf = global::Windows.Data.Pdf;
using WinStorage = global::Windows.Storage;
using WinStreams = global::Windows.Storage.Streams;
using WinImaging = global::Windows.Graphics.Imaging;

namespace Reader.App.Platform;

/// <summary>
/// Renders PDF pages with Windows.Data.Pdf (the system PDF renderer): pages on white, at an
/// exact pixel width. Used for the original-pages view, figure pages in flowing text, and OCR.
/// Documents are opened once per path and kept; every call is safe from any thread.
/// </summary>
internal sealed class WindowsPdf : IPageRasterizer
{
    private readonly ConcurrentDictionary<string, Lazy<Task<WinPdf.PdfDocument>>> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(2, 2);

    private Task<WinPdf.PdfDocument> Open(string path) =>
        _docs.GetOrAdd(path, p => new Lazy<Task<WinPdf.PdfDocument>>(async () =>
        {
            var file = await WinStorage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(p));
            return await WinPdf.PdfDocument.LoadFromFileAsync(file);
        })).Value;

    public void Forget(string path) => _docs.TryRemove(path, out _);

    /// <summary>PNG-encoded page, <paramref name="width"/> pixels wide.</summary>
    public async Task<WinStreams.InMemoryRandomAccessStream?> RenderPngAsync(string path, int pageIndex, int width)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var doc = await Open(path).ConfigureAwait(false);
            if (pageIndex < 0 || pageIndex >= doc.PageCount) return null;
            using var page = doc.GetPage((uint)pageIndex);
            var stream = new WinStreams.InMemoryRandomAccessStream();
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(width, 1, 8192),
                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            await page.RenderToStreamAsync(stream, options).AsTask().ConfigureAwait(false);
            stream.Seek(0);
            return stream;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A frozen WPF bitmap of the page (decoded off the UI thread).</summary>
    public async Task<BitmapSource?> RenderBitmapAsync(string path, int pageIndex, int width)
    {
        using var png = await RenderPngAsync(path, pageIndex, width).ConfigureAwait(false);
        if (png is null) return null;
        return await Task.Run(() =>
        {
            try
            {
                using var s = png.AsStreamForRead();
                var decoder = new PngBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32 && frame.Format != PixelFormats.Bgr32)
                {
                    frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                }
                frame.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>For OCR, on the text worker thread: blocks until the page is rendered.</summary>
    public RasterImage? Render(string pdfPath, int pageIndex, int width)
    {
        try
        {
            return Task.Run(async () =>
            {
                using var png = await RenderPngAsync(pdfPath, pageIndex, width).ConfigureAwait(false);
                if (png is null) return null;
                var decoder = await WinImaging.BitmapDecoder.CreateAsync(png);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
                var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyToBuffer(bytes.AsBuffer());
                return new RasterImage(bitmap.PixelWidth, bitmap.PixelHeight, bytes);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
