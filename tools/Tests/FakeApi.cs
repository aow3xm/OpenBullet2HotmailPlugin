using System.Net;
using System.Text;

namespace Hotmail.Tests;

/// <summary>Loopback fake of the Microsoft token endpoint (and mail API): records requests and plays scripted responses.</summary>
public sealed class FakeApi : IDisposable
{
    public record Request(string Path, string Url, string Method, string RawHeaders, IReadOnlyDictionary<string, string> Form);

    private const string DefaultBody = "{\"access_token\":\"stub-access-token\",\"expires_in\":3600}";

    private readonly HttpListener _listener = new();
    private readonly object _lock = new();
    private readonly List<Request> _requests = new();
    private readonly SemaphoreSlim _signal = new(0);

    public string BaseUrl { get; }
    public int Port { get; }
    public IReadOnlyList<Request> Requests { get { lock (_lock) { return _requests.ToList(); } } }

    /// <summary>Script to run for the next request. Takes the request URL (path and query), returns (status, body).</summary>
    public Func<string, (int Status, string Body)>? Handler { get; set; }

    public FakeApi()
    {
        var port = GetFreePort();
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        Task.Run(AcceptLoopAsync);
        WaitReadyAsync().GetAwaiter().GetResult();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (ObjectDisposedException) { }
        catch (HttpListenerException) { }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var rawHeaders = string.Join("\r\n", request.Headers.AllKeys.Select(k => $"{k}: {request.Headers[k]}"));
        string formBody = "";
        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            formBody = await reader.ReadToEndAsync();
        }
        lock (_lock)
        {
            _requests.Add(new Request(request.Url!.AbsolutePath, request.Url.PathAndQuery, request.HttpMethod, rawHeaders, ParseForm(formBody)));
        }
        _signal.Release();

        int status;
        string body;
        try
        {
            if (Handler is { } handler)
            {
                (status, body) = handler(request.Url.PathAndQuery);
            }
            else
            {
                status = 200;
                body = DefaultBody;
            }
        }
        catch (Exception ex)
        {
            // A scripted handler failure answers 500 instead of leaving the client hanging
            // until its own request timeout expires.
            status = 500;
            body = ex.Message;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            form[Uri.UnescapeDataString(key.Replace("+", " "))] = Uri.UnescapeDataString(value.Replace("+", " "));
        }
        return form;
    }

    private async Task WaitReadyAsync()
    {
        // no context until the OS listener is up; poll with a throwaway TcpClient connect
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync(System.Net.IPAddress.Loopback, new Uri(BaseUrl).Port);
                c.Close();
                return;
            }
            catch
            {
                await Task.Delay(50);
            }
        }
        throw new InvalidOperationException("FakeApi listener never came up");
    }

    /// <summary>Waits for at least <paramref name="count"/> recorded requests; throws if <paramref name="timeout"/> elapses first.</summary>
    public async Task<List<Request>> WaitForRequestsAsync(int count, TimeSpan timeout)
    {
        var got = 0;
        while (got < count)
        {
            if (!await _signal.WaitAsync(timeout))
            {
                throw new TimeoutException($"expected {count} request(s), got {got}");
            }
            got++;
        }
        lock (_lock)
        {
            return _requests.ToList();
        }
    }

    public void Dispose() => _listener.Close();
}
