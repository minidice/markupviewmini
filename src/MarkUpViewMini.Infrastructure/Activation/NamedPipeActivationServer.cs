using System.IO.Pipes;
using System.Threading.Channels;
using MarkUpViewMini.Core.Activation;

namespace MarkUpViewMini.Infrastructure.Activation;

public sealed class NamedPipeActivationServer : IActivationServer
{
    private static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(2);
    internal const int MaximumConcurrentConnections = 64;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly string pipeName;
    private readonly Func<string, PipeOptions, NamedPipeServerStream> serverFactory;
    private readonly Channel<ConnectionWork> connections = Channel.CreateUnbounded<ConnectionWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly SemaphoreSlim connectionSlots = new(
        MaximumConcurrentConnections,
        MaximumConcurrentConnections);
    private readonly object gate = new();
    private NamedPipeServerStream? currentServer;
    private Task? listener;
    private Task? dispatcher;
    private bool disposed;

    public NamedPipeActivationServer(string pipeName)
        : this(pipeName, CreateNamedPipeServer)
    {
    }

    internal NamedPipeActivationServer(
        string pipeName,
        Func<string, PipeOptions, NamedPipeServerStream> serverFactory)
    {
        this.pipeName = pipeName;
        this.serverFactory = serverFactory;
    }

    public async Task StartAsync(
        Func<ActivationRequest, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(disposed, this);

        TaskCompletionSource ready;
        lock (gate)
        {
            if (listener is not null)
            {
                throw new InvalidOperationException("The activation server is already running.");
            }

            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher = DispatchAsync(handler, lifetimeCancellation.Token);
            listener = ListenAsync(ready, lifetimeCancellation.Token);
        }

        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task? pendingListener;
        Task? pendingDispatcher;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetimeCancellation.Cancel();
            currentServer?.Dispose();
            pendingListener = listener;
            pendingDispatcher = dispatcher;
        }

        var pending = new[] { pendingListener, pendingDispatcher }
            .OfType<Task>()
            .ToArray();
        try
        {
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            connectionSlots.Dispose();
            lifetimeCancellation.Dispose();
        }
    }

    private async Task ListenAsync(
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        var signaledReady = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = CreateServer();
                try
                {
                    lock (gate)
                    {
                        currentServer = server;
                    }

                    if (!signaledReady)
                    {
                        signaledReady = true;
                        ready.TrySetResult();
                    }

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (ReferenceEquals(currentServer, server))
                        {
                            currentServer = null;
                        }
                    }

                    if (!connectionSlots.Wait(0))
                    {
                        continue;
                    }

                    var work = new ConnectionWork(
                        server,
                        ReadRequestAsync(server, cancellationToken));
                    server = null;
                    if (!connections.Writer.TryWrite(work))
                    {
                        await work.Stream.DisposeAsync().ConfigureAwait(false);
                        try
                        {
                            _ = await work.Request.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }

                        connectionSlots.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                }
                finally
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(currentServer, server))
                        {
                            currentServer = null;
                        }
                    }

                    if (server is not null)
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            throw;
        }
        finally
        {
            connections.Writer.TryComplete();
            if (!signaledReady)
            {
                ready.TrySetCanceled(cancellationToken);
            }
        }
    }

    private async Task DispatchAsync(
        Func<ActivationRequest, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        await foreach (var work in connections.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var request = await work.Request.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await handler(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await work.Stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await work.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    await work.Stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
                finally
                {
                    connectionSlots.Release();
                }
            }
        }
    }

    private static async Task<ActivationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        readCancellation.CancelAfter(ClientReadTimeout);
        var payload = await ActivationProtocol.ReadFrameAsync(stream, readCancellation.Token)
            .ConfigureAwait(false);
        return ActivationProtocol.Deserialize(payload);
    }

    private NamedPipeServerStream CreateServer() => serverFactory(
        pipeName,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static NamedPipeServerStream CreateNamedPipeServer(string name, PipeOptions options) => new(
        name,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        options);

    private sealed record ConnectionWork(
        NamedPipeServerStream Stream,
        Task<ActivationRequest> Request);
}
