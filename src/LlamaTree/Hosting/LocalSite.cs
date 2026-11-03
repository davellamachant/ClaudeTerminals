using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LlamaTree.Hosting;

/// <summary>
/// Serves the app page (wwwroot) and each plugin's folder over http://127.0.0.1:&lt;port&gt;/ for the Photino window,
/// where WebView2's virtual hosts don't exist. A loopback http address is a secure context in every engine, so the page
/// has the clipboard and the rest it has on Windows.
/// <para>
/// Only static files, read only, and nothing a local file read couldn't get: what talks to the app is the window's
/// message bridge, never this server. Requests must name this address as their Host (a site that points its own name
/// at 127.0.0.1 gets nothing), and no file allows reads from another origin. The app's files, and each plugin's
/// folder, are served under random paths made for this run: no plugin can load the app page or another plugin's page
/// into its frame (see <see cref="PluginBase"/>). The app page carries the window's bridge token, written in as it's
/// served (see <see cref="PhotinoHost"/>).
/// </para>
/// </summary>
internal sealed class LocalSite : IDisposable
{
    private const string TokenPlaceholder = "<head>";

    private readonly HttpListener listener;
    private readonly string root;
    private readonly string host;
    private readonly string appPrefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + "/";
    private readonly string bridgeToken;
    private readonly ConcurrentDictionary<string, string> pluginFolders = new();   // path segment → folder
    private readonly ConcurrentDictionary<string, string> pluginSegments = new();  // plugin id → path segment

    /// <summary>This run's key for plugins' paths, which the session host is given to work out the same addresses.</summary>
    public string PluginKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private LocalSite(HttpListener listener, int port, string root, string bridgeToken)
    {
        this.listener = listener;
        this.root = Path.GetFullPath(root);
        this.bridgeToken = bridgeToken;
        host = $"127.0.0.1:{port}";
        Origin = $"http://{host}";
        _ = AcceptLoopAsync();
    }

    /// <summary>http://127.0.0.1:&lt;port&gt;</summary>
    public string Origin { get; }

    public string AppPage => Origin + appPrefix + "index.html";

    /// <param name="bridgeToken">Written into the app page (a meta tag the page reads, then removes).</param>
    public static LocalSite Start(string root, string bridgeToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return new LocalSite(listener, port, root, bridgeToken);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();   // taken in the meantime: try another
            }
        }
    }

    /// <summary>
    /// The plugin's address: a path made from this run's key, which only the app page (that frames it) and the session
    /// host know (see <see cref="Link.PluginAddressing"/>).
    /// </summary>
    public string PluginBase(string id) =>
        $"{Origin}/plugin/{pluginSegments.GetOrAdd(id, _ => Link.PluginAddressing.Segment(PluginKey, id))}/";

    public void ServePlugin(string id, string folder)
    {
        PluginBase(id);
        pluginFolders[pluginSegments[id]] = Path.GetFullPath(folder);
    }

    public void StopServingPlugin(string id)
    {
        if (pluginSegments.TryRemove(id, out var segment)) pluginFolders.TryRemove(segment, out _);
    }

    private async Task AcceptLoopAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Serve(context));
        }
    }

    private async Task Serve(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (!string.Equals(request.Headers["Host"], host, StringComparison.OrdinalIgnoreCase) || request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = 403;
                return;
            }
            var file = Resolve(request.Url?.AbsolutePath ?? "/");
            if (file == null)
            {
                response.StatusCode = 404;
                return;
            }
            response.ContentType = ContentType(file);
            // The files only change when the app (or a plugin) does; never serve an old copy after that.
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (file == Path.Combine(root, "index.html"))
            {
                // The app page, with the bridge token for its script. Never in a frame: a frame of it would get the token too.
                response.Headers["X-Frame-Options"] = "DENY";
                response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                var html = (await File.ReadAllTextAsync(file)).Replace(TokenPlaceholder,
                    TokenPlaceholder + $"\n  <meta name=\"llamatree-bridge\" content=\"{bridgeToken}\">", StringComparison.OrdinalIgnoreCase);
                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                response.ContentLength64 = bytes.Length;
                if (request.HttpMethod == "GET") await response.OutputStream.WriteAsync(bytes);
                return;
            }
            await using var stream = File.OpenRead(file);
            response.ContentLength64 = stream.Length;
            if (request.HttpMethod == "GET") await stream.CopyToAsync(response.OutputStream);
        }
        catch (Exception)
        {
            try { response.StatusCode = 500; } catch (InvalidOperationException) { /* headers already sent */ }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>The file a request path names, inside wwwroot or a plugin's folder; null for anything else.</summary>
    private string? Resolve(string urlPath)
    {
        var path = Uri.UnescapeDataString(urlPath);
        string folder;
        if (path.StartsWith("/plugin/", StringComparison.Ordinal))
        {
            var rest = path["/plugin/".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0 || !pluginFolders.TryGetValue(rest[..slash], out var pluginFolder)) return null;
            folder = pluginFolder;
            path = rest[slash..];
        }
        else if (path.StartsWith(appPrefix, StringComparison.Ordinal))
        {
            folder = root;
            path = path[(appPrefix.Length - 1)..];
        }
        else return null;
        if (path.EndsWith('/')) path += "index.html";
        if (path.Contains('\0') || path.Contains('\\')) return null;
        var full = Path.GetFullPath(Path.Combine(folder, path.TrimStart('/')));
        var inside = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return full.StartsWith(inside, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".wasm" => "application/wasm",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
    }
}
