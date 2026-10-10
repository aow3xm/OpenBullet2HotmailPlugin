using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hotmail.Tests;

/// <summary>
/// Minimal loopback SOCKS5 proxy (no-auth, CONNECT only): records the requested target
/// host:port and relays bytes so the API response reaches the client. Used because RuriLib
/// routes ProxyType.Http through .NET's WebProxy with BypassOnLocal=true (a loopback target
/// would silently skip the proxy), while SOCKS5 goes through SocketsHttpHandler with no bypass.
/// </summary>
public sealed class FakeProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<string> _targets = new();

    public int Port { get; }
    /// <summary>First CONNECT target, as "host:port".</summary>
    public string ConnectTarget => _targets.FirstOrDefault() ?? "";

    /// <summary>
    /// When set (as "host:port"), CONNECT targets whose host is not a loopback address are
    /// connected to this endpoint instead, and the request's Host header is rewritten to match.
    /// Lets a non-local-looking target name (never bypassed by the HTTP client) reach a loopback listener.
    /// </summary>
    public string? RewriteHost { get; set; }

    public FakeProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();

        // Greeting: version(5), nmethods, methods...; we only answer the no-auth offer
        var greeting = await ReadExactAsync(stream, 2);
        if (greeting is null || greeting[0] != 5)
        {
            return;
        }
        var methods = await ReadExactAsync(stream, greeting[1]);
        if (methods is null || !methods.AsSpan().Contains((byte)0x00))
        {
            return;
        }
        await stream.WriteAsync(new byte[] { 5, 0 });
        await stream.FlushAsync();

        // CONNECT request: ver(5), cmd(1), rsv(1), atyp(1), addr, port(2)
        var head = await ReadExactAsync(stream, 4);
        if (head is null || head[0] != 5 || head[1] != 1)
        {
            return;
        }
        var atyp = head[3];
        string host;
        byte[] addrBytes;
        switch (atyp)
        {
            case 1: // IPv4
                addrBytes = await ReadExactAsync(stream, 4) ?? throw new InvalidOperationException("short IPv4 address");
                host = new IPAddress(addrBytes).ToString();
                break;
            case 3: // domain
                var len = (await ReadExactAsync(stream, 1) ?? throw new InvalidOperationException("short domain length"))[0];
                var domain = await ReadExactAsync(stream, len) ?? throw new InvalidOperationException("short domain");
                host = Encoding.ASCII.GetString(domain);
                break;
            case 4: // IPv6
                addrBytes = await ReadExactAsync(stream, 16) ?? throw new InvalidOperationException("short IPv6 address");
                host = new IPAddress(addrBytes).ToString();
                break;
            default:
                return;
        }
        var portBytes = await ReadExactAsync(stream, 2) ?? throw new InvalidOperationException("short port");
        var port = (portBytes[0] << 8) | portBytes[1];

        lock (_targets) { _targets.Add($"{host}:{port}"); }

        // Map non-local target names onto the loopback API listener, if asked to
        var connectHost = host;
        var connectPort = port;
        var rewriteHost = false;
        if (RewriteHost is { } mapped
            && !(host.StartsWith("127.") || host == "localhost" || host == "::1"))
        {
            var parts = mapped.Split(':');
            connectHost = parts[0];
            connectPort = int.Parse(parts[1]);
            rewriteHost = true;
        }

        using var origin = new TcpClient();
        await origin.ConnectAsync(connectHost, connectPort);
        await using var originStream = origin.GetStream();

        // Success reply: ver(5), rep(0), rsv(0), atyp(1), 0.0.0.0, port 0
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();

        if (rewriteHost)
        {
            // The HTTP client sent the request addressed to the original target name; the
            // loopback listener only answers requests for its own host, so rewrite Host.
            var headText = await ReadToBlankLineAsync(stream);
            var rewritten = System.Text.RegularExpressions.Regex.Replace(
                headText, @"^Host: [^\r\n]*", $"Host: {connectHost}:{connectPort}",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            await originStream.WriteAsync(Encoding.ASCII.GetBytes(rewritten));
        }

        await Task.WhenAny(RelayAsync(stream, originStream), RelayAsync(originStream, stream));
    }

    private static async Task<string> ReadToBlankLineAsync(Stream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0)
            {
                throw new IOException("closed before end of request headers");
            }
            sb.Append((char)buffer[0]);
        }
        return sb.ToString();
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0)
            {
                return read > 0 ? buffer[..read] : null;
            }
            read += n;
        }
        return buffer;
    }

    private static async Task RelayAsync(Stream from, Stream to)
    {
        var buffer = new byte[8192];
        int n;
        while ((n = await from.ReadAsync(buffer)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n));
        }
    }

    public void Dispose() => _listener.Stop();
}
