using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Depot.Tests.TestSupport;

// A real HTTP server on 127.0.0.1, for tests that go through Depot's real HTTP handler (its
// connect callback, redirect and proxy settings). Each request gets a fresh connection and
// whatever Respond returns for its path; Requests keeps the raw request heads.
public sealed class TinyHttpServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();

    public TinyHttpServer(Func<string, (int Status, string Headers, string Body)> respond)
    {
        listener.Start();
        _ = Task.Run(() => AcceptAsync(respond));
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    public ConcurrentQueue<string> Requests { get; } = [];

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
    }

    private async Task AcceptAsync(Func<string, (int Status, string Headers, string Body)> respond)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (connection)
                {
                    var stream = connection.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            return;
                        }

                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var request = head.ToString();
                    Requests.Enqueue(request);
                    var path = request.Split(' ', 3)[1];
                    var (status, headers, body) = respond(path);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} X\r\n{headers}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(bytes);
                }
            });
        }
    }
}
