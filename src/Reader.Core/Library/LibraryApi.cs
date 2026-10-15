using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Reader.Core.Sync;

namespace Reader.Core.Library;

/// <summary>A book in the account's library, as the server lists it. <see cref="RemovedAt"/> set means a tombstone.</summary>
public sealed record RemoteBook(
    string DocumentId,
    string Title,
    string Format,
    long SizeBytes,
    int PageCount,
    bool HasFile,
    long AddedAt,
    long? RemovedAt)
{
    public bool IsAlive => RemovedAt is null;
}

/// <summary>The server refused a file over its limit (413).</summary>
public sealed class LibraryTooLargeException() : Exception("too large to sync (over 100 MB)");

/// <summary>
/// The library endpoints:
/// <code>
/// GET    /v1/library             -> { books: [{documentId,title,format,sizeBytes,pageCount,hasFile,addedAt,removedAt|null}] }
/// PUT    /v1/library/{id}        { title, format: "PDF"|"EPUB", sizeBytes, pageCount } -> book (adds, or un-removes)
/// DELETE /v1/library/{id}        -> { removed }   (tombstone; file deleted)
/// PUT    /v1/library/{id}/file   raw bytes (Content-Length required, &lt;= 100 MB, SHA-256 must equal id) -> book
/// GET    /v1/library/{id}/file   -> bytes | 404
/// </code>
/// 401 throws <see cref="SyncUnauthorizedException"/>, 413 <see cref="LibraryTooLargeException"/>,
/// anything else that fails <see cref="SyncUnavailableException"/>.
/// </summary>
public interface ILibraryApi
{
    Task<List<RemoteBook>> ListAsync();
    Task<RemoteBook> PutAsync(string documentId, string title, string format, long sizeBytes, int pageCount);
    Task RemoveAsync(string documentId);
    Task<RemoteBook> UploadFileAsync(string documentId, string path);
    /// <summary>Downloads the book's bytes to <paramref name="destination"/>; false if the server has no file (404).</summary>
    Task<bool> DownloadFileAsync(string documentId, string destination);
}

public sealed class HttpLibraryApi : ILibraryApi
{
    /// <summary>The server's limit for one book file.</summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    private readonly string _baseUrl;
    private readonly Func<string?> _token;
    private readonly HttpClient _http;

    public HttpLibraryApi(string baseUrl, Func<string?> token, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Files can be large; the per-request deadline is generous and reads are streamed.
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    private string Url(string documentId, bool file = false) =>
        $"{_baseUrl}/v1/library/{Uri.EscapeDataString(documentId)}{(file ? "/file" : "")}";

    public async Task<List<RemoteBook>> ListAsync()
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/library")).ConfigureAwait(false);
        var data = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("books", out var books) || books.ValueKind != JsonValueKind.Array)
            {
                throw new SyncUnavailableException("library: no books array");
            }
            return books.EnumerateArray().Select(ParseBook).ToList();
        }
        catch (JsonException e)
        {
            throw new SyncUnavailableException("library: " + e.Message);
        }
    }

    public async Task<RemoteBook> PutAsync(string documentId, string title, string format, long sizeBytes, int pageCount)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["title"] = title,
            ["format"] = format,
            ["sizeBytes"] = sizeBytes,
            ["pageCount"] = pageCount,
        });
        var req = new HttpRequestMessage(HttpMethod.Put, Url(documentId)) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await SendAsync(req).ConfigureAwait(false);
        return await BookFrom(res).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string documentId)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, Url(documentId)), allowNotFound: true).ConfigureAwait(false);
    }

    public async Task<RemoteBook> UploadFileAsync(string documentId, string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes) throw new LibraryTooLargeException();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var req = new HttpRequestMessage(HttpMethod.Put, Url(documentId, file: true)) { Content = new StreamContent(stream, 1 << 16) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = length;
        using var res = await SendAsync(req).ConfigureAwait(false);
        return await BookFrom(res).ConfigureAwait(false);
    }

    public async Task<bool> DownloadFileAsync(string documentId, string destination)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(documentId, file: true)), allowNotFound: true,
            streaming: true).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        await using var input = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await input.CopyToAsync(output).ConfigureAwait(false);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, bool allowNotFound = false, bool streaming = false)
    {
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token() is { } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new SyncUnavailableException($"network error on {req.Method} {req.RequestUri?.AbsolutePath}: {e.Message}");
        }
        finally
        {
            req.Dispose();
        }
        if (res.IsSuccessStatusCode || (allowNotFound && res.StatusCode == HttpStatusCode.NotFound)) return res;
        var status = (int)res.StatusCode;
        res.Dispose();
        throw status switch
        {
            401 => new SyncUnauthorizedException(),
            413 => new LibraryTooLargeException(),
            _ => new SyncUnavailableException($"HTTP {status} on {req.Method} {req.RequestUri?.AbsolutePath}"),
        };
    }

    private static async Task<RemoteBook> BookFrom(HttpResponseMessage res)
    {
        var data = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            // Accept either the book itself or { book: ... }.
            if (root.TryGetProperty("book", out var inner) && inner.ValueKind == JsonValueKind.Object) root = inner;
            return ParseBook(root);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SyncUnavailableException("library: bad book: " + e.Message);
        }
    }

    public static RemoteBook ParseBook(JsonElement o)
    {
        string Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long Num(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
            : o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        long? OptNum(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? Num(k) : null;
        bool Bool(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        var id = Str("documentId");
        if (id.Length == 0) throw new SyncUnavailableException("library: book without documentId");
        return new RemoteBook(id, Str("title"), Str("format"), Num("sizeBytes"), (int)Num("pageCount"), Bool("hasFile"),
            Num("addedAt"), OptNum("removedAt"));
    }

    /// <summary>The JSON a server sends for <paramref name="b"/> (used by the fake server in tests and the self-test).</summary>
    public static string BookJson(RemoteBook b) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["documentId"] = b.DocumentId,
            ["title"] = b.Title,
            ["format"] = b.Format,
            ["sizeBytes"] = b.SizeBytes,
            ["pageCount"] = b.PageCount,
            ["hasFile"] = b.HasFile,
            ["addedAt"] = b.AddedAt,
            ["removedAt"] = b.RemovedAt,
        });
}
