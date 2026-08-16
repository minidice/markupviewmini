using MarkUpViewMini.Core.Activation;

namespace MarkUpViewMini.Infrastructure.Activation;

public interface IActivationServer : IAsyncDisposable
{
    Task StartAsync(
        Func<ActivationRequest, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
