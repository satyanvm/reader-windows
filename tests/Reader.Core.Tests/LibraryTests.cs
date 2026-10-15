using System.Security.Cryptography;
using System.Text;
using Reader.Core.Data;
using Reader.Core.Model;
using Reader.Core.Text;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

public class LibraryTests
{
    [Fact]
    public void ImportCopiesTheFileAndUsesItsSha256AsTheId()
    {
        var dir = TempDir();
        using var db = new ReaderDatabase(Path.Combine(dir, "db.sqlite"));
        var repo = new DocumentRepository(db, Path.Combine(dir, "documents"));
        var original = Path.Combine(dir, "Meditations.pdf");
        Pdfs.Make([Pdfs.BookPage(1, ["One."]), Pdfs.BookPage(2, ["Two."])], original);
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))).ToLowerInvariant();

        var doc = repo.ImportFile(original);
        Assert.Equal(expected, doc.Id);
        Assert.Equal("PDF", doc.Format);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal("Meditations", doc.Title);
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(doc.LocalPath));

        // The same bytes from somewhere else are the same book.
        var copy = Path.Combine(dir, "renamed copy.pdf");
        File.Copy(original, copy);
        File.Delete(original);
        Assert.Equal(expected, repo.ImportFile(copy).Id);
        Assert.Single(repo.Library());
        Assert.Equal("Not started · 2 pages", repo.Library()[0].ProgressLabel);
        Assert.True(File.Exists(doc.LocalPath)); // readable after the original is gone
    }

    [Fact]
    public void PdfMetadataTitleWinsOverTheFileName()
    {
        var dir = TempDir();
        using var db = new ReaderDatabase(Path.Combine(dir, "db.sqlite"));
        var repo = new DocumentRepository(db, Path.Combine(dir, "documents"));
        var path = Path.Combine(dir, "scan_0001.pdf");
        Pdfs.Make([Pdfs.BookPage(1, ["One."])], path, title: "Letters from a Stoic");
        Assert.Equal("Letters from a Stoic", repo.ImportFile(path).Title);
    }

    [Fact]
    public void EpubsAreImportedWithTheirTitleAndChapterCount()
    {
        var dir = TempDir();
        using var db = new ReaderDatabase(Path.Combine(dir, "db.sqlite"));
        var repo = new DocumentRepository(db, Path.Combine(dir, "documents"));
        var path = Path.Combine(dir, "book.epub");
        Zips.Write(EpubTests.Book.Select(f => (f.Item1, Encoding.UTF8.GetBytes(f.Item2))), path, deflate: true);
        var doc = repo.ImportFile(path);
        Assert.Equal("EPUB", doc.Format);
        Assert.Equal("The Meditations", doc.Title);
        Assert.Equal(2, doc.PageCount);

        var positions = new ReadingPositionRepository(db, new HybridClock(), "windows");
        positions.SaveLocal(new ReadingPosition { DocumentId = doc.Id, PageIndex = 1, PageOffset = 0.5f, PageCount = 2 });
        Assert.Equal("Chapter 2 of 2 · 75%", repo.Library()[0].ProgressLabel);
    }

    [Fact]
    public void ProgressLabelsReadLikeTheOtherApps()
    {
        Assert.Equal("Page 187 of 500 · 37%", new LibraryRow("x", "t", 500, 0, 186, 0.5f, "PDF").ProgressLabel);
        Assert.Equal("Chapter 2 of 12 · 12%", new LibraryRow("x", "t", 12, 0, 1, 0.5f, "EPUB").ProgressLabel);
        Assert.Equal("Not started · 500 pages", new LibraryRow("x", "t", 500, 0, null, null, "PDF").ProgressLabel);
    }

    [Fact]
    public void OtherFilesAreRejected()
    {
        var dir = TempDir();
        using var db = new ReaderDatabase(Path.Combine(dir, "db.sqlite"));
        var repo = new DocumentRepository(db, Path.Combine(dir, "documents"));
        var path = Path.Combine(dir, "notes.txt");
        File.WriteAllText(path, "hello");
        Assert.Throws<UnsupportedFormatException>(() => repo.ImportFile(path));
        Assert.Empty(repo.Library());
    }

    [Fact]
    public void LibraryIsOrderedByLastOpened()
    {
        var dir = TempDir();
        using var db = new ReaderDatabase(Path.Combine(dir, "db.sqlite"));
        long now = 1000;
        var repo = new DocumentRepository(db, Path.Combine(dir, "documents"), () => now);
        var a = Path.Combine(dir, "a.pdf");
        var b = Path.Combine(dir, "b.pdf");
        Pdfs.Make([Pdfs.BookPage(1, ["A."])], a);
        Pdfs.Make([Pdfs.BookPage(1, ["B."])], b);
        var da = repo.ImportFile(a);
        now = 2000;
        repo.ImportFile(b);
        Assert.Equal(["b", "a"], repo.Library().Select(r => r.Title));
        now = 3000;
        repo.Touch(da.Id);
        Assert.Equal(["a", "b"], repo.Library().Select(r => r.Title));
    }
}
