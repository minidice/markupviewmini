using System.IO.Pipes;
using MarkUpViewMini.Core.Activation;

namespace MarkUpViewMini.Infrastructure.Activation;

public sealed class NamedPipeActivationClient
{
    private static readonly TimeSpan DefaultEndToEndTimeout = TimeSpan.FromSeconds(5);
    private readonly string pipeName;
    private readonly TimeSpan endToEndTimeout;

    public NamedPipeActivationClient(string pipeName)
        : this(pipeName, DefaultEndToEndTimeout)
    {
    }

    internal NamedPipeActivationClient(string pipeName, TimeSpan endToEndTimeout)
    {
        this.pipeName = pipeName;
        if (endToEndTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(endToEndTimeout));
        }

        this.endToEndTimeout = endToEndTimeout;
    }

    public async Task ForwardAsync(
        ActivationRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ActivationProtocol.Serialize(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(endToEndTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await ActivationProtocol.WriteFrameAsync(pipe, payload, deadline.Token).ConfigureAwait(false);

            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            if (acknowledgement[0] != 1)
            {
                throw new InvalidDataException("The primary process returned an invalid acknowledgement.");
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The primary process did not acknowledge activation before the deadline.", exception);
        }
    }
}
