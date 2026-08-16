namespace MarkUpViewMini.Infrastructure.Diagnostics;

public interface ISafeLogger
{
    void Write(string component, string eventName, string? path, Exception? error);
}
