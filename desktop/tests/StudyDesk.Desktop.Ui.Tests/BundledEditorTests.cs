using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Prevents successful desktop builds whose installed editor cannot start offline.</summary>
public sealed partial class BundledEditorTests
{
    /// <summary>Checks the copied entry point and every local CSS/JavaScript reference in the actual output.</summary>
    [Fact]
    public void OutputContainsEntryPointAndEveryReferencedAsset()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "editor");
        string entryPoint = Path.Combine(directory, "index.html");
        Assert.True(File.Exists(entryPoint), "The desktop output must contain editor/index.html.");
        string content = File.ReadAllText(entryPoint);
        MatchCollection references = AssetReferencePattern().Matches(content);
        Assert.True(references.Count >= 2, "The editor must reference its bundled script and stylesheet.");
        foreach (Match reference in references)
        {
            string relative = reference.Groups[1].Value;
            Assert.StartsWith("./assets/", relative, StringComparison.Ordinal);
            string asset = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, asset, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(asset), "A locally referenced editor asset is missing from the desktop output.");
            Assert.True(new FileInfo(asset).Length > 0, "Editor assets cannot be empty.");
        }
    }

    /// <summary>Finds the production bundle references while ignoring unrelated document attributes.</summary>
    [GeneratedRegex("(?:src|href)=\"([^\"]+\\.(?:js|css))\"", RegexOptions.CultureInvariant)]
    private static partial Regex AssetReferencePattern();
}
