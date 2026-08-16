using System.Runtime.CompilerServices;

namespace MarkUpViewMini.App.Tests.Activation;

public sealed class ActivationSmokeScriptTests
{
    [Fact]
    public void Failure_path_throws_the_sanitized_message_only()
    {
        var scriptPath = Path.Combine(
            Path.GetDirectoryName(CurrentSourcePath())!,
            "Run-TwoProcessActivationSmoke.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("throw $message", script, StringComparison.Ordinal);
        Assert.DoesNotContain("throw $exception", script, StringComparison.Ordinal);
    }

    private static string CurrentSourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}
