using KidsLocker.Service.Enforcement;
using Xunit;

namespace KidsLocker.Tests;

public class ServiceControlTests
{
    private const string Exe = @"C:\Program Files\KidsLocker\KidsLocker.exe";

    [Fact]
    public void BuildBinPath_WrapsExeInEscapedInnerQuotes()
    {
        // The path contains a space (Program Files); the exe must stay a single token after the
        // command-line parser splits sc.exe's arguments. Correct form begins with "\" (quote,
        // backslash, quote), NOT "" (the old doubled-quote bug that split the path).
        var live = ServiceControl.BuildBinPath(Exe, dryRun: false);
        Assert.StartsWith("\"\\\"", live);
        Assert.Contains($"\\\"{Exe}\\\"", live);
        Assert.DoesNotContain("--dry-run", live);
    }

    [Fact]
    public void BuildBinPath_DryRun_AppendsFlagInsideOuterQuotes()
    {
        var dry = ServiceControl.BuildBinPath(Exe, dryRun: true);
        Assert.StartsWith("\"\\\"", dry);
        Assert.Contains($"\\\"{Exe}\\\" --dry-run", dry);
        Assert.EndsWith("\"", dry);
    }
}
