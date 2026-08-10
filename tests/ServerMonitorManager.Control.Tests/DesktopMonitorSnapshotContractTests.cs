using System.Text.RegularExpressions;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class DesktopMonitorSnapshotContractTests
{
    [Fact]
    public void QueryParserFieldNamesMatchCanonicalSnapshot()
    {
        var root = FindRepositoryRoot();
        var canonicalKeys = ReadKeys(Path.Combine(root, "tests", "contracts", "monitor-snapshot-v1.txt"));
        var source = File.ReadAllText(Path.Combine(
            root, "src", "ServerMonitorManager.Desktop", "SshMonitorService.cs"));
        var queryStart = source.IndexOf("public async Task<ServerMetrics> QueryAsync(", StringComparison.Ordinal);
        var queryEnd = source.IndexOf(
            "public async Task<string> RunRestrictedCommandAsync(", queryStart, StringComparison.Ordinal);
        Assert.True(queryStart >= 0 && queryEnd > queryStart, "Desktop QueryAsync source was not found.");

        var querySource = source[queryStart..queryEnd];
        var parserKeys = Regex.Matches(querySource, "\"([A-Z][A-Z0-9_]*)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // PROTOCOL gates the wire format and KERNEL is diagnostic metadata; the current
        // ServerMetrics model intentionally does not project either value.
        var unprojectedKeys = new HashSet<string>(["PROTOCOL", "KERNEL"], StringComparer.Ordinal);
        Assert.Subset(canonicalKeys, unprojectedKeys);
        Assert.Equal(
            canonicalKeys.Except(unprojectedKeys).Order(StringComparer.Ordinal),
            parserKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void InstallerDocumentationListsCanonicalSnapshotFields()
    {
        var root = FindRepositoryRoot();
        var canonicalKeys = ReadKeys(Path.Combine(root, "tests", "contracts", "monitor-snapshot-v1.txt"));
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "installer-contract.md"));
        var sectionStart = documentation.IndexOf("## 7. Forced command monitoring", StringComparison.Ordinal);
        var sectionEnd = documentation.IndexOf("## 8.", sectionStart, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && sectionEnd > sectionStart, "Installer contract section 7 was not found.");

        var documentedKeys = Regex.Matches(
                documentation[sectionStart..sectionEnd],
                "^([A-Z][A-Z0-9_]*)=",
                RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            canonicalKeys.Order(StringComparer.Ordinal),
            documentedKeys.Order(StringComparer.Ordinal));
    }

    private static HashSet<string> ReadKeys(string path)
        => File.ReadAllLines(path)
            .Select(line => line.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitorManager.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
