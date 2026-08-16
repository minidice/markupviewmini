using System.Text;
using MarkUpViewMini.App.ViewModels;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class DocumentOpenErrorViewModelTests
{
    [Fact]
    public void Decoder_failure_exposes_only_encoding_and_close_actions()
    {
        const string secret = "TOP-SECRET-BODY";

        var error = DocumentOpenErrorViewModel.From(new DecoderFallbackException(secret));

        Assert.True(error.CanChooseEncoding);
        Assert.False(error.CanRetry);
        Assert.False(error.CanSaveAs);
        Assert.True(error.CanClose);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetryableFailures))]
    public void File_access_failure_exposes_only_retry_and_close_actions(Func<Exception> createFailure)
    {
        const string secret = "TOP-SECRET-PATH";

        var error = DocumentOpenErrorViewModel.From(createFailure());

        Assert.True(error.CanRetry);
        Assert.False(error.CanChooseEncoding);
        Assert.False(error.CanSaveAs);
        Assert.True(error.CanClose);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Func<Exception>> RetryableFailures => new()
    {
        () => new FileNotFoundException("TOP-SECRET-PATH"),
        () => new DirectoryNotFoundException("TOP-SECRET-PATH"),
        () => new UnauthorizedAccessException("TOP-SECRET-PATH"),
        () => new IOException("TOP-SECRET-PATH"),
    };
}
