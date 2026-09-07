using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

internal sealed class Socks5Proxy(ProxySettings settings)
{
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(settings.Host, settings.Port, cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);
            await NegotiateAsync(stream, context.DnsEndPoint, cancellationToken);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task NegotiateAsync(Stream stream, DnsEndPoint target, CancellationToken cancellationToken)
    {
        var useCredentials = !string.IsNullOrEmpty(settings.Username);
        var methods = useCredentials ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(methods, cancellationToken);
        var selection = new byte[2];
        await ReadExactlyAsync(stream, selection, cancellationToken);
        if (selection[0] != 0x05 || selection[1] == 0xFF) throw new IOException("SOCKS5 代理没有可用的认证方式。");
        if (selection[1] == 0x02) await AuthenticateAsync(stream, cancellationToken);

        var hostBytes = Encoding.ASCII.GetBytes(target.Host);
        if (hostBytes.Length > 255) throw new IOException("SOCKS5 目标主机名过长。");
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5 + hostBytes.Length), (ushort)target.Port);
        await stream.WriteAsync(request, cancellationToken);

        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (header[0] != 0x05 || header[1] != 0x00) throw new IOException($"SOCKS5 代理连接失败，状态码 0x{header[1]:X2}。");
        var addressLength = header[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadLengthAsync(stream, cancellationToken),
            _ => throw new IOException("SOCKS5 代理返回了未知地址类型。")
        };
        await ReadExactlyAsync(stream, new byte[addressLength + 2], cancellationToken);
    }

    private async Task AuthenticateAsync(Stream stream, CancellationToken cancellationToken)
    {
        var username = Encoding.UTF8.GetBytes(settings.Username);
        var password = Encoding.UTF8.GetBytes(settings.Password);
        if (username.Length > 255 || password.Length > 255) throw new IOException("SOCKS5 用户名或密码过长。");
        var request = new byte[3 + username.Length + password.Length];
        request[0] = 0x01;
        request[1] = (byte)username.Length;
        username.CopyTo(request, 2);
        request[2 + username.Length] = (byte)password.Length;
        password.CopyTo(request, 3 + username.Length);
        await stream.WriteAsync(request, cancellationToken);
        var response = new byte[2];
        await ReadExactlyAsync(stream, response, cancellationToken);
        if (response[1] != 0x00) throw new IOException("SOCKS5 代理认证失败。");
    }

    private static async Task<int> ReadLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await ReadExactlyAsync(stream, value, cancellationToken);
        return value[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("SOCKS5 代理意外关闭了连接。");
            offset += read;
        }
    }
}
