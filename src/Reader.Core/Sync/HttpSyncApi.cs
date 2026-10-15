using System.Net;
using System.Net.Http.Headers;
using Reader.Core.Model;

namespace Reader.Core.Sync;

/// <summary>
/// REST implementation of <see cref="ISyncApi"/>:
/// <code>
///     GET {base}/v1/reading-positions/{documentId}  -> 200 position | 404
///     PUT {base}/v1/reading-positions/{documentId}  body: position -> 200 winning position
/// </code>
/// </summary>
public sealed class HttpSyncApi : ISyncApi
{
    private readonly string _baseUrl;
    private readonly Func<string?> _token;
    private readonly HttpClient _http;

    public HttpSyncApi(string baseUrl, Func<string?> token, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public Task<ReadingPosition?> GetAsync(string documentId) => RequestAsync(HttpMethod.Get, documentId, null);

    public async Task<ReadingPosition> PutAsync(ReadingPosition position) =>
        await RequestAsync(HttpMethod.Put, position.DocumentId, PositionJson.Encode(position))
        ?? throw new SyncUnavailableException("empty PUT response");

    private async Task<ReadingPosition?> RequestAsync(HttpMethod method, string documentId, byte[]? body)
    {
        var url = $"{_baseUrl}/v1/reading-positions/{Uri.EscapeDataString(documentId)}";
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token() is { } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        HttpResponseMessage response;
        byte[] data;
        try
        {
            response = await _http.SendAsync(req).ConfigureAwait(false);
            data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new SyncUnavailableException($"network error on {method}: {e.Message}");
        }
        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    try { return PositionJson.Decode(data); }
                    catch (PositionJson.DecodeException e) { throw new SyncUnavailableException(e.Message); }
                case HttpStatusCode.NotFound when method == HttpMethod.Get:
                    return null;
                case HttpStatusCode.Unauthorized:
                    throw new SyncUnauthorizedException();
                default:
                    throw new SyncUnavailableException($"HTTP {(int)response.StatusCode} on {method}");
            }
        }
    }
}
