namespace MarkUpViewMini.Core.Documents;

public sealed record TextChange(int From, int To, string InsertedText);
