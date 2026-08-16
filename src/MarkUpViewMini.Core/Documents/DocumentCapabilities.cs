namespace MarkUpViewMini.Core.Documents;

[Flags]
public enum DocumentCapabilities
{
    None = 0,
    Read = 1,
    Edit = 2,
    FileNameSearch = 4,
    BodySearch = 8,
    InternalLinks = 16
}
