using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkMountStudio;

/// <summary>
/// Serves the settings page and a tiny JSON API on http://localhost:4173, loopback only.
///   GET  /api/state   { settings, status, clock }   clock = the effects' time in seconds, for the page's preview
///   PUT  /api/state   any subset of the settings; answers like GET
///   GET  /api/levels  { levels: [32 floats], beat }   beat = number of beats heard so far
///   anything else     the page: /, /index.html, /styles.css, /js/*.js
/// </summary>
sealed partial class HttpServer : IDisposable
{
    public const int Port = 4173;
    public static readonly string Url = $"http://localhost:{Port}";
    static readonly string[] AllowedHosts = [$"localhost:{Port}", $"127.0.0.1:{Port}"];
    static readonly string[] AllowedOrigins = [$"http://localhost:{Port}", $"http://127.0.0.1:{Port}"];
    const int MaxBodyBytes = 1 << 20;

    readonly SettingsStore _store;
    readonly Engine _engine;
    readonly string? _siteFolder; // null: serve the files embedded in the exe
    HttpListener? _listener;

    public bool Running => _listener != null;

    public HttpServer(SettingsStore store, Engine engine, string? siteFolder)
    {
        _store = store;
        _engine = engine;
        _siteFolder = siteFolder;
    }

    /// <summary>Returns false (and stays off) when the port cannot be used.</summary>
    public bool Start()
    {
        // 127.0.0.1 is a second name for the same page; if Windows refuses it, localhost alone will do.
        foreach (string[] hosts in new[] { AllowedHosts, AllowedHosts[..1] })
        {
            var listener = new HttpListener();
            try
            {
                foreach (string host in hosts) listener.Prefixes.Add($"http://{host}/");
                listener.Start();
                _listener = listener;
                _ = Task.Run(() => Loop(listener));
                return true;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        return false;
    }

    async Task Loop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener closed
            _ = Task.Run(() =>
            {
                try { Handle(ctx); }
                catch { try { ctx.Response.Abort(); } catch { /* connection already gone */ } }
            });
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";

        // Only answer requests addressed to this page itself: stops other websites from reaching
        // the API through DNS tricks or cross-site requests.
        if (!AllowedHosts.Contains(req.Headers["Host"] ?? "", StringComparer.OrdinalIgnoreCase))
        {
            Send(res, 403, "text/plain; charset=utf-8", "Forbidden");
            return;
        }
        string path = req.Url?.AbsolutePath ?? "/";
        if (path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal))
        {
            string? origin = req.Headers["Origin"];
            if (origin != null && !AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                Send(res, 403, "text/plain; charset=utf-8", "Forbidden");
                return;
            }
            Api(req, res, path);
        }
        else
        {
            Static(req, res, path);
        }
    }

    void Api(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        switch (path, req.HttpMethod)
        {
            case ("/api/state", "GET"):
                Send(res, 200, "application/json", StateJson());
                break;
            case ("/api/state", "PUT"):
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(ReadBody(req));
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    Send(res, 400, "text/plain; charset=utf-8", "Bad request");
                    break;
                }
                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        Send(res, 400, "text/plain; charset=utf-8", "Bad request");
                        break;
                    }
                    _store.Update(s => s.Apply(doc.RootElement));
                }
                Send(res, 200, "application/json", StateJson());
                break;
            case ("/api/levels", "GET"):
                Send(res, 200, "application/json", JsonSerializer.Serialize(new { levels = _engine.Levels, beat = _engine.Beat }));
                break;
            case ("/api/state", _) or ("/api/levels", _):
                Send(res, 405, "text/plain; charset=utf-8", "Method not allowed");
                break;
            default:
                Send(res, 404, "text/plain; charset=utf-8", "Not found");
                break;
        }
    }

    string StateJson() =>
        JsonSerializer.Serialize(new { settings = _store.Current, status = _engine.Status, clock = _engine.Now }, Settings.JsonCompact);

    static byte[] ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBodyBytes) throw new InvalidDataException("Body too large");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = req.InputStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            body.Write(buffer, 0, n);
            if (body.Length > MaxBodyBytes) throw new InvalidDataException("Body too large");
        }
        return body.ToArray();
    }

    // The only files the page consists of. Nothing else is ever read, so no path can leave the site.
    [GeneratedRegex(@"^/(index\.html|styles\.css|js/[A-Za-z0-9_\-][A-Za-z0-9_.\-]*\.js)$")]
    private static partial Regex SitePath();

    void Static(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        if (req.HttpMethod is not ("GET" or "HEAD"))
        {
            Send(res, 405, "text/plain; charset=utf-8", "Method not allowed");
            return;
        }
        if (path == "/") path = "/index.html";
        byte[]? data = SitePath().IsMatch(path) ? ReadSiteFile(path[1..]) : null;
        if (data == null)
        {
            Send(res, 404, "text/plain; charset=utf-8", "Not found");
            return;
        }
        string type = path.EndsWith(".html") ? "text/html; charset=utf-8"
            : path.EndsWith(".css") ? "text/css; charset=utf-8"
            : "text/javascript; charset=utf-8";
        Send(res, 200, type, data, req.HttpMethod == "HEAD");
    }

    byte[]? ReadSiteFile(string relative)
    {
        if (_siteFolder != null)
        {
            string file = Path.Combine(_siteFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("site/" + relative);
        if (stream == null) return null;
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    static void Send(HttpListenerResponse res, int status, string contentType, string body) =>
        Send(res, status, contentType, Encoding.UTF8.GetBytes(body));

    static void Send(HttpListenerResponse res, int status, string contentType, byte[] body, bool headOnly = false)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = body.Length;
        if (!headOnly) res.OutputStream.Write(body, 0, body.Length);
        res.Close();
    }

    public void Dispose()
    {
        var listener = _listener;
        _listener = null;
        try { listener?.Close(); } catch { /* already closed */ }
    }
}
