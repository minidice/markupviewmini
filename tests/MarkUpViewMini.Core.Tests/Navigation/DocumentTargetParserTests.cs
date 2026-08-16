using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.Core.Tests.Navigation;

public sealed class DocumentTargetParserTests
{
    [Theory]
    [InlineData(@"C:\Docs\guide.md", @"C:\Docs\guide.md", null, null)]
    [InlineData(@"C:\Docs\guide.md:123", @"C:\Docs\guide.md", 123, null)]
    [InlineData(@"C:\Docs\guide.md#install", @"C:\Docs\guide.md", null, "install")]
    [InlineData(@"C:\Docs\guide.md:123#install", @"C:\Docs\guide.md", 123, "install")]
    [InlineData(@"C:\Docs\guide.md#install#advanced", @"C:\Docs\guide.md", null, "install#advanced")]
    [InlineData(@"C:\Docs\release:notes.md", @"C:\Docs\release:notes.md", null, null)]
    [InlineData(@"C:\Docs\guide.md:123-notes", @"C:\Docs\guide.md:123-notes", null, null)]
    public void Parse_preserves_path_colons_and_extracts_only_terminal_line_suffix(
        string input, string expectedPath, int? line, string? anchor)
    {
        var target = DocumentTargetParser.Parse(input, null);

        Assert.Equal(expectedPath, target.Path);
        Assert.Equal(line, target.Line);
        Assert.Equal(anchor, target.Anchor);
    }

    [Fact]
    public void Parse_resolves_relative_path_against_document_directory()
    {
        var target = DocumentTargetParser.Parse(@"chapter\two.md:7", @"C:\Docs");

        Assert.Equal(@"C:\Docs\chapter\two.md", target.Path);
        Assert.Equal(7, target.Line);
        Assert.Null(target.Anchor);
    }

    [Fact]
    public void Parse_rejects_an_empty_path()
    {
        Assert.Throws<FormatException>(() => DocumentTargetParser.Parse(string.Empty, @"C:\Docs"));
    }

    [Fact]
    public void Parse_rejects_line_zero()
    {
        Assert.Throws<FormatException>(() => DocumentTargetParser.Parse(@"C:\Docs\guide.md:0", null));
    }

    [Fact]
    public void Parse_rejects_a_nul_character()
    {
        Assert.Throws<FormatException>(() => DocumentTargetParser.Parse("guide.md\0:7", @"C:\Docs"));
    }
}
