using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PerfSentinelHub.Tests.Support;

/// <summary>
///     A loopback SMTP relay that records what it is handed. It answers every command it does not list
///     with 250, so the client never waits out its timeout, and lets a test choose the AUTH mechanisms it
///     advertises and the reply to RCPT.
/// </summary>
public sealed class FakeSmtp : IAsyncDisposable
{
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task _accepting = Task.CompletedTask;

    private FakeSmtp(TcpListener listener)
    {
        _listener = listener;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string AuthMechanisms { get; set; } = "LOGIN";

    public string? RcptReply { get; set; }

    /// <summary>The mechanism of each AUTH command the client sent, lowercased, in order.</summary>
    public ConcurrentQueue<string> AuthAttempts { get; } = new();

    public ConcurrentQueue<Message> Received { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await Task.WhenAll([_accepting, .. _connections]).WaitAsync(TimeSpan.FromSeconds(5))
            .ContinueWith(_ => { }, TaskScheduler.Default);
        _stop.Dispose();
    }

    public static Task<FakeSmtp> StartAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var smtp = new FakeSmtp(listener);
        smtp._accepting = smtp.AcceptAsync();
        return Task.FromResult(smtp);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ServeAsync(client, _stop.Token));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                              or SocketException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false))
                { NewLine = "\r\n", AutoFlush = true };
                await Converse(reader, writer, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                              or ObjectDisposedException)
        {
            // The client left or the fake stopped.
        }
    }

    private async Task Converse(StreamReader reader, StreamWriter writer, CancellationToken cancellationToken)
    {
        string? user = null;
        var to = new List<string>();
        await writer.WriteLineAsync("220 fake");
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var words = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (words.Length == 0 ? "" : words[0].ToUpperInvariant())
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync($"250 AUTH {AuthMechanisms}");
                    break;
                case "AUTH":
                    if (words.Length > 1)
                        AuthAttempts.Enqueue(words[1].ToLowerInvariant());
                    user = await AuthenticateAsync(words, reader, writer, cancellationToken) ?? user;
                    break;
                case "RCPT":
                    var reply = RcptReply ?? "250 OK";
                    if (reply.StartsWith("250", StringComparison.Ordinal))
                        to.Add(line[(line.IndexOf('<') + 1)..line.IndexOf('>')]);
                    await writer.WriteLineAsync(reply);
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go on");
                    Received.Enqueue(new Message(user, [.. to], await ReadDataAsync(reader, cancellationToken)));
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }
    }

    // SmtpClient writes the mechanism in lowercase; anything but LOGIN is refused, as a relay without GSSAPI does.
    private static async Task<string?> AuthenticateAsync(
        string[] words, StreamReader reader, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (words.Length < 3 || !words[1].Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync("504 5.5.4 not supported");
            return null;
        }

        var user = Encoding.UTF8.GetString(Convert.FromBase64String(words[2]));
        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
        await reader.ReadLineAsync(cancellationToken);
        await writer.WriteLineAsync("235 ok");
        return user;
    }

    private static async Task<string> ReadDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line && line != ".")
            data.Append(line).Append('\n');
        return data.ToString();
    }

    public sealed record Message(string? AuthUser, IReadOnlyList<string> To, string Data);
}
