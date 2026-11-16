using System.Xml.Linq;

namespace StudyLife.App.Core.Tests;

/// <summary>
/// 2026-11-16 audit, M10: Android backed up the WebView profile that holds the session token. The
/// manifest and the extraction rules are plain files, so this pins them where every CI run sees it
/// (the Android build itself only proves they are well-formed).
/// </summary>
public class AndroidBackupPolicyTests
{
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    private static readonly string[] Domains =
    [
        "root", "file", "database", "sharedpref", "external",
        "device_root", "device_file", "device_database", "device_sharedpref",
    ];

    private static string AndroidDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "studylife.version")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "StudyLife.App", "Platforms", "Android");
    }

    [Fact]
    public void TheManifest_SwitchesBackupOff_AndPointsAtTheExtractionRules()
    {
        var application = XDocument.Load(Path.Combine(AndroidDir(), "AndroidManifest.xml")).Root!.Element("application")!;

        Assert.Equal("false", (string?)application.Attribute(Android + "allowBackup"));
        Assert.Equal("false", (string?)application.Attribute(Android + "fullBackupContent"));
        Assert.Equal("@xml/data_extraction_rules", (string?)application.Attribute(Android + "dataExtractionRules"));
    }

    [Theory]
    [InlineData("cloud-backup")]
    [InlineData("device-transfer")]
    public void TheExtractionRules_ExcludeEveryDomain(string section)
    {
        var rules = XDocument.Load(Path.Combine(AndroidDir(), "Resources", "xml", "data_extraction_rules.xml")).Root!;
        var block = rules.Element(section);

        Assert.NotNull(block);
        Assert.Empty(block!.Elements("include"));
        var excluded = block.Elements("exclude")
            .Where(e => (string?)e.Attribute("path") == ".")
            .Select(e => (string?)e.Attribute("domain"))
            .ToHashSet();
        Assert.All(Domains, domain => Assert.Contains(domain, excluded));
    }
}
