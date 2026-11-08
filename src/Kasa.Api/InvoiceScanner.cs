using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Kasa.Api;

internal sealed class InvoiceScanner(IConfiguration configuration, ILogger<InvoiceScanner> logger) : IDisposable
{
    private readonly SemaphoreSlim slots = new(4);

    public async Task<string> Scan(byte[] contents, CancellationToken ct)
    {
        var host = configuration["InvoiceScanner:Host"] ?? "127.0.0.1";
        var port = configuration.GetValue("InvoiceScanner:Port", 3310);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var entered = false;
        try
        {
            await slots.WaitAsync(token);
            entered = true;
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), token);
            var size = new byte[4];
            for (var offset = 0; offset < contents.Length; offset += 64 * 1024)
            {
                var length = Math.Min(64 * 1024, contents.Length - offset);
                BinaryPrimitives.WriteInt32BigEndian(size, length);
                await stream.WriteAsync(size, token);
                await stream.WriteAsync(contents.AsMemory(offset, length), token);
            }
            Array.Clear(size);
            await stream.WriteAsync(size, token);

            var reply = new byte[512];
            var position = 0;
            while (position < reply.Length)
            {
                var count = await stream.ReadAsync(reply.AsMemory(position), token);
                if (count == 0)
                    break;
                var end = Array.IndexOf(reply, (byte)0, position, count);
                if (end >= 0)
                {
                    var answer = Encoding.ASCII.GetString(reply, 0, end);
                    if (answer == "stream: OK")
                        return "clean";
                    if (answer.StartsWith("stream: ", StringComparison.Ordinal) && answer.EndsWith(" FOUND", StringComparison.Ordinal))
                        return "rejected";
                    break;
                }
                position += count;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Invoice scanner timed out");
            return "pending";
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            logger.LogWarning("Invoice scanner connection failed");
            return "pending";
        }
        finally
        {
            if (entered)
                slots.Release();
        }
        logger.LogWarning("Invoice scanner returned an unexpected reply");
        return "pending";
    }

    public void Dispose() => slots.Dispose();
}
