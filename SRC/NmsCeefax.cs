using System.Net;
using System.Text;
using System.Xml.Linq;

namespace BBC;

internal sealed class NmsCeefax : IDisposable
{
    internal const string FeedUrl = "https://feeds.nmsni.co.uk/svn/ceefax/London/";
    internal static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BBC_MODEL_B", "Teletext", "NmsLondon");
    private readonly string cacheDirectory;
    private readonly CancellationTokenSource cancellation = new();
    private readonly HttpClient http;
    private TeletextPage[]? pendingPages;
    private string? notice;
    private string? error;

    internal NmsCeefax(string? cacheDirectory = null, HttpMessageHandler? httpHandler = null)
    {
        this.cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;
        http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.MaxResponseContentBufferSize = 512 * 1024;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BBC-Model-B-Teletext/1.0");
        _ = RunAsync();
    }

    internal TeletextPage[]? TakePages() => Interlocked.Exchange(ref pendingPages, null);
    internal string? TakeNotice() => Interlocked.Exchange(ref notice, null);
    internal string? TakeError() => Interlocked.Exchange(ref error, null);

    private async Task RunAsync()
    {
        CancellationToken token = cancellation.Token;
        bool havePages = false;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            TeletextPage[] cached = ReadCache();
            if (cached.Length != 0)
            {
                Interlocked.Exchange(ref pendingPages, cached);
                havePages = true;
                Interlocked.Exchange(ref notice, "Using cached NMS Ceefax; checking for updates");
            }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string index = await http.GetStringAsync(FeedUrl, token);
                    // The SVN directory is XML. Only accept simple page filenames,
                    // never paths or arbitrary URLs supplied by the remote listing.
                    string[] files = XDocument.Parse(index).Descendants("file")
                        .Select(x => (string?)x.Attribute("name"))
                        .Where(x => x is not null && IsPageFile(x)).Cast<string>().Distinct().Take(512).ToArray();
                    if (files.Length == 0) throw new InvalidDataException("NMS Ceefax returned no pages.");
                    await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (file, ct) =>
                    {
                        string path = Path.Combine(cacheDirectory, file);
                        using HttpRequestMessage request = new(HttpMethod.Get, FeedUrl + file);
                        if (File.Exists(path)) request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(path);
                        using HttpResponseMessage response = await http.SendAsync(request, ct);
                        if (response.StatusCode == HttpStatusCode.NotModified) return;
                        response.EnsureSuccessStatusCode();
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                        if (!Encoding.Latin1.GetString(bytes).Split('\n').Any(line => line.StartsWith("PN,", StringComparison.Ordinal)))
                            throw new InvalidDataException($"NMS Ceefax returned an invalid page: {file}");
                        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, bytes, ct);
                            ct.ThrowIfCancellationRequested();
                            File.Move(temporary, path, overwrite: true);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        if (response.Content.Headers.LastModified is { } modified) File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
                    });
                    TeletextPage[] pages = files.SelectMany(file => TeletextPage.Parse(File.ReadAllText(Path.Combine(cacheDirectory, file), Encoding.Latin1))).ToArray();
                    if (pages.Length == 0) throw new InvalidDataException("NMS Ceefax has no active pages.");
                    Interlocked.Exchange(ref pendingPages, pages);
                    havePages = true;
                    // Remove withdrawn pages only after a complete successful refresh.
                    foreach (string old in Directory.EnumerateFiles(cacheDirectory, "*.tti"))
                        if (!files.Contains(Path.GetFileName(old), StringComparer.Ordinal)) File.Delete(old);
                    Interlocked.Exchange(ref error, null);
                    Interlocked.Exchange(ref notice, "NMS Ceefax updated; preset 1 is ready");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Xml.XmlException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested) break;
                    string reason = ex switch
                    {
                        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                        HttpRequestException => "connection failed",
                        OperationCanceledException => "request timed out",
                        InvalidDataException or System.Xml.XmlException => "invalid feed",
                        _ => "download failed"
                    };
                    string fallback = havePages ? "Using cached pages" : "No cached pages available";
                    Interlocked.Exchange(ref notice, null);
                    Interlocked.Exchange(ref error, $"NMS Ceefax: {reason}. {fallback}. Retrying in 5 min.");
                    Console.WriteLine($"Teletext feed error ({FeedUrl}): {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref notice, null);
            Interlocked.Exchange(ref error, "Teletext cache unavailable: " + ex.Message);
        }
        finally
        {
            http.Dispose();
            cancellation.Dispose();
        }
    }

    private TeletextPage[] ReadCache() => Directory.EnumerateFiles(cacheDirectory, "*.tti")
        .Where(path => IsPageFile(Path.GetFileName(path))).Take(512)
        .SelectMany(path => TeletextPage.Parse(File.ReadAllText(path, Encoding.Latin1))).ToArray();

    private static bool IsPageFile(string file) => file.Length == 8 && file[0] == 'P'
        && file.EndsWith(".tti", StringComparison.OrdinalIgnoreCase)
        && file.AsSpan(1, 3).ToArray().All(Uri.IsHexDigit);

    public void Dispose()
    {
        // Cancellation interrupts downloads without waiting on the UI thread.
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
