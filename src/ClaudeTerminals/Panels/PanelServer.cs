using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeTerminals.Panels;

/// <summary>
/// The MCP endpoint each session's Claude talks to (Streamable HTTP, answering every POST with plain JSON), on
/// 127.0.0.1 at a port picked at start. Every session gets its own bearer token, which is how a call is tied to the
/// session that made it and why nothing else on the machine can use the endpoint.
/// </summary>
internal sealed class PanelServer : IDisposable
{
    private const int MaxBody = 1 << 20;

    private readonly HttpListener listener;
    private readonly PanelMcp mcp;
    private readonly ConcurrentDictionary<string, string> sessionsByToken = new();
    private readonly ConcurrentDictionary<string, string> tokensBySession = new();

    private PanelServer(HttpListener listener, int port, PanelMcp mcp)
    {
        this.listener = listener;
        this.mcp = mcp;
        Url = $"http://127.0.0.1:{port}/mcp";
        _ = AcceptLoopAsync();
    }

    public string Url { get; }

    /// <summary>Starts on a free port; null if none could be had (sessions then start without panels).</summary>
    public static PanelServer? TryStart(PanelMcp mcp)
    {
        for (var attempt = 0; attempt < 5; attempt++)
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
                return new PanelServer(listener, port, mcp);
            }
            catch (HttpListenerException)
            {
                listener.Close();   // taken in the meantime: try another
            }
        }
        return null;
    }

    /// <summary>The session's token (made once per run of the app).</summary>
    public string TokenFor(string session) => tokensBySession.GetOrAdd(session, s =>
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessionsByToken[token] = s;
        return token;
    });

    /// <summary>A removed session's token stops working.</summary>
    public void Forget(string session)
    {
        if (tokensBySession.TryRemove(session, out var token)) sessionsByToken.TryRemove(token, out _);
    }

    /// <summary>The --mcp-config file for a session: this server, with the session's token.</summary>
    public string ConfigJson(string session) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [PanelMcp.ServerName] = new
            {
                type = "http",
                url = Url,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + TokenFor(session) },
            },
        },
    });

    private async Task AcceptLoopAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath != "/mcp") { Send(response, 404); return; }
            // A web page could reach 127.0.0.1 too; Claude Code's requests carry no Origin.
            if (request.Headers["Origin"] != null) { Send(response, 403); return; }
            var auth = request.Headers["Authorization"];
            if (auth == null || !auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !sessionsByToken.TryGetValue(auth["Bearer ".Length..].Trim(), out var session))
            {
                Send(response, 401);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // No server-to-client stream (GET) or session to end (DELETE): everything is answered in the POST.
                response.AddHeader("Allow", "POST");
                Send(response, 405);
                return;
            }
            if (request.ContentLength64 > MaxBody) { Send(response, 413); return; }

            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await request.InputStream.ReadAsync(chunk)) > 0)
            {
                if (body.Length + n > MaxBody) { Send(response, 413); return; }
                body.Write(chunk, 0, n);
            }

            System.Text.Json.Nodes.JsonObject? reply;
            try
            {
                using var doc = JsonDocument.Parse(body.ToArray());
                reply = mcp.Handle(session, doc.RootElement);
            }
            catch (JsonException)
            {
                reply = PanelMcp.Error(null, -32700, "Parse error");
            }
            if (reply == null) { Send(response, 202); return; }
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        catch (Exception)
        {
            try { Send(response, 500); } catch (Exception) { /* the client has gone */ }
        }
    }

    private static void Send(HttpListenerResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentLength64 = 0;
        response.Close();
    }

    public void Dispose()
    {
        try { listener.Close(); } catch (ObjectDisposedException) { }
    }
}
