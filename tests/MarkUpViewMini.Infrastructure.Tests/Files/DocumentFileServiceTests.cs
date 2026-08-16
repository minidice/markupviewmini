using System.Text;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.Infrastructure.Tests.Files;

public sealed class DocumentFileServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly DocumentFileService _service = new();

    public DocumentFileServiceTests()
    {
        DocumentFileService.RegisterCodePages();
        _directory = Path.Combine(
            Path.GetTempPath(),
            nameof(DocumentFileServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "document.md");
    }

    [Fact]
    public async Task LoadAsync_decodes_strict_bomless_utf8()
    {
        const string text = "# 제목\n본문";
        await File.WriteAllBytesAsync(_path, new UTF8Encoding(false, true).GetBytes(text));

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(text, document.Text);
        Assert.Equal(new EncodingDescriptor("utf-8", false), document.Encoding);
    }

    [Fact]
    public async Task LoadAsync_decodes_utf8_bom_without_returning_the_bom()
    {
        const string text = "# heading";
        var encoding = new UTF8Encoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(_path, bytes);

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(text, document.Text);
        Assert.Equal(new EncodingDescriptor("utf-8", true), document.Encoding);
    }

    [Fact]
    public async Task LoadAsync_decodes_utf16_little_endian_bom()
    {
        const string text = "작은 엔디언";
        var encoding = new UnicodeEncoding(false, true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(_path, bytes);

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(text, document.Text);
        Assert.Equal(new EncodingDescriptor("utf-16", true), document.Encoding);
    }

    [Fact]
    public async Task LoadAsync_decodes_utf16_big_endian_bom()
    {
        const string text = "큰 엔디언";
        var encoding = new UnicodeEncoding(true, true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(_path, bytes);

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(text, document.Text);
        Assert.Equal(new EncodingDescriptor("utf-16BE", true), document.Encoding);
    }

    [Theory]
    [InlineData("first\r\nsecond\r\n", NewLineKind.CrLf)]
    [InlineData("first\nsecond\n", NewLineKind.Lf)]
    [InlineData("first\rsecond\r", NewLineKind.Cr)]
    [InlineData("first\r\nsecond\nthird\r", NewLineKind.Mixed)]
    public async Task LoadAsync_classifies_newlines(string text, NewLineKind expected)
    {
        await File.WriteAllTextAsync(_path, text, new UTF8Encoding(false, true));

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(expected, document.NewLine);
    }

    [Theory]
    [InlineData("first\r\nsecond\r\nthird\nfour", "\r\n")]
    [InlineData("first\nsecond\r\nthird", "\n")]
    [InlineData("first\rsecond\nthird", "\r")]
    [InlineData("no newline", "\n")]
    public async Task LoadAsync_selects_the_most_frequent_newline_with_first_occurrence_tie_break(
        string text,
        string expectedPreferredNewLine)
    {
        await File.WriteAllTextAsync(_path, text, new UTF8Encoding(false, true));

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(expectedPreferredNewLine, document.PreferredNewLine);
    }

    [Fact]
    public async Task LoadAsync_captures_exact_original_file_metadata()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62];
        await File.WriteAllBytesAsync(_path, bytes);
        File.SetLastWriteTimeUtc(_path, new DateTime(2025, 4, 3, 2, 1, 0, DateTimeKind.Utc));
        var expectedLastWriteUtc = new FileInfo(_path).LastWriteTimeUtc;

        var document = await _service.LoadAsync(_path, CancellationToken.None);

        Assert.Equal(bytes.LongLength, document.Version.Length);
        Assert.Equal(expectedLastWriteUtc, document.Version.LastWriteTimeUtc);
        Assert.Equal(
            "bb7fe77b9185814610698ca2785e861545e8f45abee4e4244133f0e0bddb431f",
            document.Version.Sha256);
    }

    [Fact]
    public async Task LoadAsync_uses_a_legacy_encoding_only_when_explicitly_selected()
    {
        const string text = "한글";
        var encoding = Encoding.GetEncoding(949);
        await File.WriteAllBytesAsync(_path, encoding.GetBytes(text));

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => _service.LoadAsync(_path, CancellationToken.None));

        var document = await _service.LoadAsync(_path, encoding, CancellationToken.None);

        Assert.Equal(text, document.Text);
        Assert.Equal(new EncodingDescriptor("ks_c_5601-1987", false), document.Encoding);
    }

    [Fact]
    public async Task LoadAsync_rejects_invalid_bomless_utf8_without_replacement_characters()
    {
        await File.WriteAllBytesAsync(_path, [0xC3, 0x28]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => _service.LoadAsync(_path, CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_with_selected_windows949_rejects_malformed_input()
    {
        await File.WriteAllBytesAsync(_path, [0x81]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => _service.LoadAsync(
                _path,
                Encoding.GetEncoding(949),
                CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_with_selected_utf8_rejects_malformed_input()
    {
        await File.WriteAllBytesAsync(_path, [0xC3, 0x28]);

        await Assert.ThrowsAsync<DecoderFallbackException>(
            () => _service.LoadAsync(_path, Encoding.UTF8, CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_propagates_cancellation()
    {
        await File.WriteAllTextAsync(_path, "content");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.LoadAsync(_path, cancellation.Token));
    }

    [Fact]
    public async Task LoadAsync_with_selected_encoding_propagates_cancellation()
    {
        await File.WriteAllTextAsync(_path, "content");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.LoadAsync(_path, Encoding.UTF8, cancellation.Token));
    }

    [Fact]
    public async Task LoadAsync_runs_cpu_processing_without_the_callers_synchronization_context()
    {
        await File.WriteAllTextAsync(_path, new string('x', 128_000));
        SynchronizationContext? processingContext = null;
        var service = new DocumentFileService(checkpoint =>
        {
            if (checkpoint == DocumentProcessingCheckpoint.BeforeDecode)
            {
                processingContext = SynchronizationContext.Current;
            }
        });
        var callerContext = new PreservingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<LoadedDocument> loading;
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            loading = service.LoadAsync(_path, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await loading;

        Assert.Null(processingContext);
    }

    [Fact]
    public async Task LoadAsync_observes_cancellation_during_hashing()
    {
        await File.WriteAllTextAsync(_path, new string('x', 256_000));
        using var cancellation = new CancellationTokenSource();
        var hashCheckpoints = 0;
        var service = new DocumentFileService(checkpoint =>
        {
            if (checkpoint == DocumentProcessingCheckpoint.HashChunk &&
                ++hashCheckpoints == 1)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LoadAsync(_path, cancellation.Token));

        Assert.Equal(1, hashCheckpoints);
    }

    [Fact]
    public async Task LoadAsync_observes_cancellation_after_the_last_cpu_pass()
    {
        await File.WriteAllTextAsync(_path, "first\nsecond\n");
        using var cancellation = new CancellationTokenSource();
        var service = new DocumentFileService(checkpoint =>
        {
            if (checkpoint == DocumentProcessingCheckpoint.AfterNewLineScan)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LoadAsync(_path, cancellation.Token));
    }

    [Fact]
    public async Task LoadAsync_observes_inflight_newline_cancellation_when_crlf_skips_chunk_boundaries()
    {
        var characters = Enumerable.Repeat('x', 196_700).ToArray();
        foreach (var boundary in new[] { 65_536, 131_072, 196_608 })
        {
            characters[boundary - 1] = '\r';
            characters[boundary] = '\n';
        }

        await File.WriteAllTextAsync(_path, new string(characters));
        using var cancellation = new CancellationTokenSource();
        var scannedCharacters = 0;
        var service = new DocumentFileService(checkpoint =>
        {
            if (checkpoint == DocumentProcessingCheckpoint.NewLineCharacter &&
                ++scannedCharacters == 2)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LoadAsync(_path, cancellation.Token));

        Assert.InRange(scannedCharacters, 2, 70_000);
    }

    [Fact]
    public async Task LoadAsync_propagates_file_system_errors()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.LoadAsync(_path, CancellationToken.None));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private sealed class PreservingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                try
                {
                    SetSynchronizationContext(this);
                    callback(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
        }
    }
}
