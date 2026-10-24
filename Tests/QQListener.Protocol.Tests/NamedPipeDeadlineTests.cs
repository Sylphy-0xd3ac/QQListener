using System.IO.Pipes;
using QQListener.Pipes;

namespace QQListener.Protocol.Tests;

public sealed class NamedPipeDeadlineTests
{
    [Fact]
    public async Task UnresponsivePeerCannotKeepAControlReadAlive()
    {
        var name = "q" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        using var transport = new NamedPipeTransport(name, readTimeoutMs: 200);
        await accepted.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() => transport.Read(new byte[40]))
            .WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task UnresponsivePeerCannotKeepAControlWriteAlive()
    {
        var name = "q" + Guid.NewGuid().ToString("N")[..12];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 1024, 1024);
        var accepted = server.WaitForConnectionAsync();
        using var transport = new NamedPipeTransport(name, readTimeoutMs: 200);
        await accepted.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() => transport.Write(new byte[4 * 1024 * 1024]))
            .WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
