namespace MarkUpViewMini.Core.Activation;

public sealed record ActivationRequest(
    int Version,
    ActivationKind Kind,
    IReadOnlyList<string> Paths,
    int SenderProcessId);
