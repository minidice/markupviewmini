namespace MarkUpViewMini.Infrastructure.Time;

public interface IClock
{
    DateTime UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
