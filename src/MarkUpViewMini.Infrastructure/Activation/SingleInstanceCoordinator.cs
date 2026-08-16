using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MarkUpViewMini.Core.Activation;

namespace MarkUpViewMini.Infrastructure.Activation;

public enum SingleInstanceResult
{
    Primary,
    Forwarded,
}

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultReelectionTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReelectionPollInterval = TimeSpan.FromMilliseconds(25);
    private readonly Func<ActivationRequest, CancellationToken, Task> handler;
    private readonly Func<string, IActivationServer> serverFactory;
    private readonly Func<string, ActivationRequest, CancellationToken, Task> forwardRequest;
    private readonly TimeSpan reelectionTimeout;
    private Mutex? mutex;
    private IActivationServer? server;
    private bool started;
    private bool disposed;

    public SingleInstanceCoordinator(
        string applicationId,
        Func<ActivationRequest, CancellationToken, Task> handler)
        : this(
            applicationId,
            handler,
            static pipeName => new NamedPipeActivationServer(pipeName),
            static (pipeName, request, cancellationToken) =>
                new NamedPipeActivationClient(pipeName).ForwardAsync(request, cancellationToken),
            DefaultReelectionTimeout)
    {
    }

    internal SingleInstanceCoordinator(
        string applicationId,
        Func<ActivationRequest, CancellationToken, Task> handler,
        Func<string, IActivationServer> serverFactory)
        : this(
            applicationId,
            handler,
            serverFactory,
            static (pipeName, request, cancellationToken) =>
                new NamedPipeActivationClient(pipeName).ForwardAsync(request, cancellationToken),
            DefaultReelectionTimeout)
    {
    }

    internal SingleInstanceCoordinator(
        string applicationId,
        Func<ActivationRequest, CancellationToken, Task> handler,
        Func<string, IActivationServer> serverFactory,
        Func<string, ActivationRequest, CancellationToken, Task> forwardRequest,
        TimeSpan reelectionTimeout)
    {
        if (string.IsNullOrWhiteSpace(applicationId) ||
            applicationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("The application ID contains unsupported characters.", nameof(applicationId));
        }

        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
        this.forwardRequest = forwardRequest ?? throw new ArgumentNullException(nameof(forwardRequest));
        if (reelectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reelectionTimeout));
        }

        this.reelectionTimeout = reelectionTimeout;
        var scopedName = $"{applicationId}-{CurrentUserSuffix}";
        MutexName = $@"Local\{scopedName}";
        PipeName = scopedName;
    }

    internal static string CurrentUserSuffix { get; } = CreateCurrentUserSuffix();

    internal string MutexName { get; }

    internal string PipeName { get; }

    public async Task<SingleInstanceResult> StartOrForwardAsync(
        ActivationRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            throw new InvalidOperationException("The coordinator can only be started once.");
        }

        request = ActivationProtocol.ValidateAndNormalize(request);
        started = true;
        if (TryAcquirePrimaryMutex())
        {
            return await StartPrimaryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await forwardRequest(PipeName, request, cancellationToken).ConfigureAwait(false);
            return SingleInstanceResult.Forwarded;
        }
        catch (Exception forwardException) when (
            !cancellationToken.IsCancellationRequested &&
            forwardException is not InvalidDataException)
        {
            if (await TryReelectPrimaryAsync(cancellationToken).ConfigureAwait(false))
            {
                return await StartPrimaryAsync(cancellationToken).ConfigureAwait(false);
            }

            ExceptionDispatchInfo.Capture(forwardException).Throw();
            throw;
        }
    }

    private async Task<SingleInstanceResult> StartPrimaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            server = serverFactory(PipeName);
            await server.StartAsync(handler, cancellationToken).ConfigureAwait(false);
            return SingleInstanceResult.Primary;
        }
        catch
        {
            try
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                server = null;
                ReleaseMutex();
            }

            throw;
        }
    }

    private async Task<bool> TryReelectPrimaryAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(reelectionTimeout);
        while (!deadline.IsCancellationRequested)
        {
            if (TryAcquirePrimaryMutex())
            {
                return true;
            }

            try
            {
                await Task.Delay(ReelectionPollInterval, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private bool TryAcquirePrimaryMutex()
    {
        var candidate = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            return false;
        }

        mutex = candidate;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            server = null;
            ReleaseMutex();
        }
    }

    private void ReleaseMutex()
    {
        mutex?.Dispose();
        mutex = null;
    }

    private static string CreateCurrentUserSuffix()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("The current Windows SID is unavailable.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();
    }
}
