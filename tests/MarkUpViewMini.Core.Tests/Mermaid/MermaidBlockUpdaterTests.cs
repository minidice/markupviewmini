using System.Security.Cryptography;
using System.Text;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Mermaid;

namespace MarkUpViewMini.Core.Tests.Mermaid;

public sealed class MermaidBlockUpdaterTests
{
    private static readonly DiskFileVersion InitialVersion =
        new(6, DateTime.UnixEpoch, new string('a', 64));

    [Fact]
    public void TryApply_replaces_only_the_original_block_as_one_revision()
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->B\nafter");
        var snapshot = SnapshotFor(
            buffer,
            "flowchart LR\nA-->B");

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->C");

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Equal("before\nflowchart LR\nA-->C\nafter", buffer.Text);
        Assert.Equal(snapshot.DocumentRevision + 1, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_stale_document_revision_without_mutation()
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->B\nafter");
        var snapshot = SnapshotFor(
            buffer,
            "flowchart LR\nA-->B");
        buffer.Apply(new DocumentEdit(0, [new TextChange(0, 0, "changed\n")]));

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->C");

        Assert.Equal(MermaidApplyResult.StaleRevision, result);
        Assert.Equal("changed\nbefore\nflowchart LR\nA-->B\nafter", buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_changed_original_range_without_mutation()
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->C\nafter");
        var snapshot = CreateSnapshot(
            buffer,
            "flowchart LR\nA-->B",
            from: 7,
            to: 25);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->D");

        Assert.Equal(MermaidApplyResult.RangeChanged, result);
        Assert.Equal("before\nflowchart LR\nA-->C\nafter", buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(7, 100)]
    public void TryApply_rejects_offsets_outside_the_current_document_without_mutation(
        int from,
        int to)
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->B\nafter");
        var snapshot = CreateSnapshot(
            buffer,
            "flowchart LR\nA-->B",
            from,
            to);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->C");

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal("before\nflowchart LR\nA-->B\nafter", buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_source_hash_mismatch_without_mutation()
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->B\nafter");
        var original = SnapshotFor(buffer, "flowchart LR\nA-->B");
        var snapshot = original with { SourceHash = new string('0', 64) };

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->C");

        Assert.Equal(MermaidApplyResult.RangeChanged, result);
        Assert.Equal("before\nflowchart LR\nA-->B\nafter", buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("flowchart LR\n```\nA-->C")]
    [InlineData("flowchart LR\n~~~\nA-->C")]
    public void TryApply_rejects_replacement_fence_delimiters_without_mutation(string replacement)
    {
        var buffer = CreateBuffer("before\nflowchart LR\nA-->B\nafter");
        var snapshot = SnapshotFor(buffer, "flowchart LR\nA-->B");

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal("before\nflowchart LR\nA-->B\nafter", buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_blockquote_contained_fence_delimiter_without_mutation()
    {
        var buffer = CreateBuffer(
            "before\n> ~~~mermaid\n> flowchart LR\n> A-->B\n> ~~~\nafter");
        var snapshot = SnapshotFor(buffer, "> flowchart LR\n> A-->B");

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "> flowchart LR\n> ~~~\n> injected");

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(
            "before\n> ~~~mermaid\n> flowchart LR\n> A-->B\n> ~~~\nafter",
            buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData(">\t")]
    [InlineData("> \t")]
    [InlineData(">\t>\t")]
    public void TryApply_rejects_tab_prefixed_blockquote_fence_delimiters_without_mutation(
        string prefix)
    {
        var source = $"{prefix}flowchart LR\n{prefix}A-->B";
        var original = $"before\n{prefix}~~~mermaid\n{source}\n{prefix}~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            $"{prefix}flowchart LR\n{prefix}~~~\n{prefix}injected");

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_accepts_tab_prefixed_blockquote_mermaid_content()
    {
        const string prefix = ">\t";
        var source = $"{prefix}flowchart LR\n{prefix}A-->B";
        var buffer = CreateBuffer(
            $"before\n{prefix}~~~mermaid\n{source}\n{prefix}~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            $"{prefix}flowchart LR\n{prefix}A-->C");

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Equal(
            $"before\n{prefix}~~~mermaid\n{prefix}flowchart LR\n{prefix}A-->C\n{prefix}~~~\nafter",
            buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData("> ")]
    [InlineData("> > ")]
    [InlineData("> \t")]
    public void TryApply_rejects_replacement_that_strips_the_original_container_prefix(
        string prefix)
    {
        var source = $"{prefix}flowchart LR\n{prefix}A-->B";
        var original = $"before\n~~~mermaid\n{source}\n~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "flowchart LR\nA-->C");

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData("> ")]
    [InlineData("> > ")]
    [InlineData("> \t")]
    public void TryApply_accepts_replacement_that_retains_the_exact_container_prefix(
        string prefix)
    {
        var source = $"{prefix}flowchart LR\n{prefix}A-->B";
        var buffer = CreateBuffer($"before\n~~~mermaid\n{source}\n~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            $"{prefix}flowchart RL\n{prefix}A-->C");

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains($"{prefix}flowchart RL\n{prefix}A-->C", buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("  flowchart LR\r\n  A-->B", "  flowchart RL\n  A-->C")]
    [InlineData("  flowchart LR\n  A-->B", "  flowchart RL\r\n  A-->C")]
    public void TryApply_rejects_replacement_that_changes_the_physical_newline_kind(
        string source,
        string replacement)
    {
        var original = $"before\n~~~mermaid\n{source}\n~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_one_line_escaping_a_mixed_tab_space_list_context()
    {
        const string source = "    flowchart LR\r\n\tA-->B\r\n    B-->C";
        const string replacement = "    flowchart RL\r\nA-->B\r\n    B-->C";
        var original = $"before\n- ~~~mermaid\n{source}\n    ~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_accepts_mixed_equivalent_prefixes_with_the_original_newline_kind()
    {
        const string source = "    flowchart LR\r\n\tA-->B\r\n    B-->C";
        const string replacement = "    flowchart RL\r\n\tA-->B\r\n\tstyle A fill:#e3f2fd,stroke:#1565c0,color:#0d3c74\r\n    B-->C";
        var buffer = CreateBuffer($"before\n- ~~~mermaid\n{source}\n    ~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_fence_escape_with_a_tab_equivalent_list_prefix()
    {
        const string source = "    flowchart LR\n\tA-->B";
        var original = $"before\n- ~~~mermaid\n{source}\n    ~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(
            buffer,
            snapshot,
            "    flowchart LR\n\t~~~\n\tinjected");

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_preserves_a_blank_physical_line_inside_a_mixed_prefix_context()
    {
        const string source = "    flowchart LR\r\n\r\n\tA(Round)-->B";
        const string replacement = "    flowchart LR\r\n\r\n\tA(Changed)-->B";
        var buffer = CreateBuffer($"before\n- ~~~mermaid\n{source}\n    ~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Fact]
    public void TryApply_accepts_unchanged_source_when_the_header_has_extra_logical_indentation()
    {
        const string source = "    flowchart LR\n  A --> B";
        var buffer = CreateBuffer($"before\n- ~~~mermaid\n{source}\n  ~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, source);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(source, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("- ~~~mermaid\n", "    flowchart LR\n  A --> B", "    flowchart RL\n  A --> B", "\n  ~~~")]
    [InlineData("  - ~~~mermaid\n", "        flowchart LR\n    A --> B", "        flowchart RL\n    A --> B", "\n    ~~~")]
    [InlineData("> ~~~mermaid\n", ">   flowchart LR\n> A --> B", ">   flowchart RL\n> A --> B", "\n> ~~~")]
    [InlineData("- ~~~mermaid\r\n", "        flowchart LR\r\n\tA --> B", "        flowchart RL\r\n\tA --> B", "\r\n    ~~~")]
    [InlineData("> ~~~mermaid\r\n", ">     flowchart LR\r\n>\tA --> B", ">     flowchart RL\r\n>\tA --> B", "\r\n> ~~~")]
    public void TryApply_accepts_token_only_replacement_using_the_minimum_shared_structural_context(
        string opening,
        string source,
        string replacement,
        string closing)
    {
        var buffer = CreateBuffer($"before\n{opening}{source}{closing}\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("    flowchart LR\n  A --> B", "    flowchart RL\n A --> B")]
    [InlineData("        flowchart LR\n    A --> B", "        flowchart RL\n   A --> B")]
    [InlineData(">   flowchart LR\n> A --> B", ">   flowchart RL\nA --> B")]
    [InlineData("        flowchart LR\r\n\tA --> B", "        flowchart RL\r\n   A --> B")]
    [InlineData(">     flowchart LR\r\n>\tA --> B", ">     flowchart RL\r\n>  A --> B")]
    public void TryApply_rejects_any_line_that_escapes_the_minimum_shared_structural_context(
        string source,
        string replacement)
    {
        var original = $"before\n~~~mermaid\n{source}\n~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_fence_escape_at_the_minimum_context_below_an_extra_indented_header()
    {
        const string source = "    flowchart LR\n  A --> B";
        const string replacement = "    flowchart LR\n  ~~~\n  injected";
        var original = $"before\n- ~~~mermaid\n{source}\n  ~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("~~~", "~~~")]
    [InlineData("```", "```")]
    [InlineData("~~~~~", "~~~~~~")]
    [InlineData("`````", "``````")]
    public void TryApply_rejects_a_shifted_quote_marker_that_closes_the_containing_fence(
        string outerDelimiter,
        string injectedDelimiter)
    {
        const string source = ">   flowchart LR\n> A --> B";
        var original = $"before\n> {outerDelimiter}mermaid\n{source}\n> {outerDelimiter}\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);
        var replacement = $">   flowchart LR\n    > {injectedDelimiter}\n    > injected";

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("~~~", "~~~")]
    [InlineData("```", "```")]
    [InlineData("~~~~~", "~~~~~~")]
    [InlineData("`````", "``````")]
    public void TryApply_rejects_a_quote_delimiter_after_optional_padding(
        string outerDelimiter,
        string injectedDelimiter)
    {
        const string source = ">flowchart LR\n>A --> B";
        var original = $"before\n>{outerDelimiter}mermaid\n{source}\n>{outerDelimiter}\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);
        var replacement = $">flowchart LR\n>    {injectedDelimiter}\n>injected";

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_rejects_a_quote_delimiter_when_a_bare_quote_sets_the_minimum_context()
    {
        const string source = "> flowchart LR\n>\n> A --> B";
        const string replacement = "> flowchart LR\n>\n>    ~~~\n>injected";
        var original = $"before\n>~~~mermaid\n{source}\n>~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData(">>")]
    [InlineData("> >")]
    public void TryApply_rejects_a_nested_quote_delimiter_after_optional_padding(string prefix)
    {
        var source = $"{prefix}flowchart LR\n{prefix}A --> B";
        var original = $"before\n{prefix}~~~mermaid\n{source}\n{prefix}~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);
        var replacement = $"{prefix}flowchart LR\n{prefix}    ~~~\n{prefix}injected";

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("   ")]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData(" \t")]
    [InlineData("  \t")]
    public void TryApply_rejects_quote_fences_at_zero_through_three_logical_columns(
        string padding)
    {
        const string source = ">flowchart LR\n>A --> B";
        var original = $"before\n>~~~mermaid\n{source}\n>~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);
        var replacement = $">flowchart LR\n>{padding}~~~\n>injected";

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData("     ")]
    [InlineData("   \t")]
    public void TryApply_accepts_quote_fences_at_four_or_more_logical_columns(string padding)
    {
        const string source = ">flowchart LR\n>A --> B";
        var buffer = CreateBuffer($"before\n>~~~mermaid\n{source}\n>~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);
        var replacement = $">flowchart RL\n>{padding}~~~\n>injected";

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("> ", ">  ", ">     ", "~~~")]
    [InlineData("> ", ">   ", ">     ", "```")]
    [InlineData("> ", ">    ", ">     ", "~~~~~~")]
    [InlineData("> ", ">   ", ">     ", "``````")]
    [InlineData("> > ", "> >   ", "> >     ", "~~~~~")]
    [InlineData("> ", ">\t", ">\t  ", "```")]
    [InlineData("  > ", "  >\t  ", "  >\t    ", "~~~~~")]
    public void TryApply_accepts_absolute_four_column_quote_fence_content_with_a_positive_source_minimum(
        string outerPrefix,
        string sourcePrefix,
        string delimiterPrefix,
        string delimiter)
    {
        var source = $"{sourcePrefix}flowchart LR\n{sourcePrefix}A --> B";
        var original = $"before\n{outerPrefix}~~~mermaid\n{source}\n{outerPrefix}~~~\nafter";
        var replacement = $"{sourcePrefix}flowchart RL\n{delimiterPrefix}{delimiter}\n{sourcePrefix}injected";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Equal(original.Replace(source, replacement, StringComparison.Ordinal), buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData("> ", ">  ", ">  ", "~~~")]
    [InlineData("> ", ">  ", ">   ", "```")]
    [InlineData("> ", ">  ", ">    ", "~~~~~")]
    [InlineData("> ", ">   ", ">    ", "`````")]
    [InlineData("> > ", "> >   ", "> >    ", "~~~")]
    [InlineData("> ", ">\t", ">\t ", "```")]
    public void TryApply_rejects_absolute_zero_through_three_column_quote_fences_with_a_positive_source_minimum(
        string outerPrefix,
        string sourcePrefix,
        string delimiterPrefix,
        string delimiter)
    {
        var source = $"{sourcePrefix}flowchart LR\n{sourcePrefix}A --> B";
        var original = $"before\n{outerPrefix}~~~mermaid\n{source}\n{outerPrefix}~~~\nafter";
        var replacement = $"{sourcePrefix}flowchart LR\n{delimiterPrefix}{delimiter}\n{sourcePrefix}injected";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Fact]
    public void TryApply_accepts_normal_content_after_optional_quote_padding()
    {
        const string source = "> flowchart LR\n> A --> B";
        const string replacement = ">   flowchart RL\n>    A --> C";
        var buffer = CreateBuffer($"before\n>~~~mermaid\n{source}\n>~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Theory]
    [InlineData(
        ">   flowchart LR\n> A --> B",
        ">   flowchart LR\n    > A --> C")]
    [InlineData(
        ">   flowchart LR\n> A --> B",
        ">   flowchart LR\n> > A --> C")]
    [InlineData(
        ">   flowchart LR\n> A --> B",
        ">   flowchart LR\nA --> C")]
    [InlineData(
        "  >   flowchart LR\n  > A --> B",
        ">   flowchart LR\n>   A --> C")]
    [InlineData(
        "> >   flowchart LR\n> > A --> B",
        ">     > flowchart LR\n>     > A --> C")]
    public void TryApply_rejects_under_over_or_shifted_structural_quote_markers(
        string source,
        string replacement)
    {
        var original = $"before\n~~~mermaid\n{source}\n~~~\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData(
        "> >   flowchart LR\n> > A --> B",
        "> > ~~~mermaid\n",
        "\n> > ~~~",
        "> >   flowchart LR\n>     > ~~~\n>     > injected")]
    [InlineData(
        "    >   flowchart LR\n    > A --> B",
        "- ~~~mermaid\n",
        "\n    ~~~",
        "    >   flowchart LR\n        > ~~~\n        > injected")]
    [InlineData(
        "\t> \tflowchart LR\n\t> \tA --> B",
        "- ~~~mermaid\n",
        "\n    ~~~",
        "\t> \tflowchart LR\n\t      > ~~~\n\t      > injected")]
    public void TryApply_rejects_shifted_nested_list_and_tab_quote_fence_escapes(
        string source,
        string opening,
        string closing,
        string replacement)
    {
        var original = $"before\n{opening}{source}{closing}\nafter";
        var buffer = CreateBuffer(original);
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.InvalidRange, result);
        Assert.Equal(original, buffer.Text);
        Assert.Equal(0, buffer.Revision);
    }

    [Theory]
    [InlineData(
        "    >   flowchart LR\n    > A --> B",
        "\t>   flowchart RL\n\t> A --> B")]
    [InlineData(
        "    > >   flowchart LR\n    > > A --> B",
        "\t> >   flowchart RL\n\t> > A --> B")]
    public void TryApply_accepts_equivalent_tabs_and_spaces_at_the_same_quote_marker_columns(
        string source,
        string replacement)
    {
        var buffer = CreateBuffer($"before\n- ~~~mermaid\n{source}\n    ~~~\nafter");
        var snapshot = SnapshotFor(buffer, source);

        var result = MermaidBlockUpdater.TryApply(buffer, snapshot, replacement);

        Assert.Equal(MermaidApplyResult.Applied, result);
        Assert.Contains(replacement, buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    private static MermaidBlockSnapshot SnapshotFor(DocumentBuffer buffer, string source)
    {
        var from = buffer.Text.IndexOf(source, StringComparison.Ordinal);
        return CreateSnapshot(buffer, source, from, from + source.Length);
    }

    private static MermaidBlockSnapshot CreateSnapshot(
        DocumentBuffer buffer,
        string source,
        int from,
        int to) =>
        new(
            Guid.NewGuid(),
            buffer.TabId,
            buffer.Revision,
            from,
            to,
            source,
            SourceHash(source));

    private static string SourceHash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static DocumentBuffer CreateBuffer(string text) =>
        DocumentBuffer.Create(
            Guid.NewGuid(),
            "document.md",
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Mixed,
            "\r\n",
            InitialVersion);
}
