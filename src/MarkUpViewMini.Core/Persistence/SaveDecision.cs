using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Persistence;

public abstract record SaveDecision
{
    private SaveDecision()
    {
    }

    public sealed record Normal : SaveDecision;

    public sealed record UseMyVersion(DiskFileVersion ObservedCurrent) : SaveDecision;

    public sealed record SaveAs(string TargetPath, EncodingDescriptor Encoding) : SaveDecision;
}
