using System.Text.Json;
using PiloSync.Cli.Update;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed class ReleaseSelectionTests
{
    [Theory]
    [InlineData("v2026.1116a", true)]
    [InlineData("v2027.0101z", true)]
    [InlineData("v2026.1116A", false)]
    [InlineData("v2026.1316a", false)]
    [InlineData("v2026.1100a", false)]
    [InlineData("v2026.1116aa", false)]
    public void ValidatesTag(string tag, bool valid) => Assert.Equal(valid, ReleaseSelection.IsTag(tag));

    [Fact]
    public void SelectsNewestCompleteStableRelease()
    {
        object Release(string tag, bool draft = false, bool prerelease = false, bool complete = true) => new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { new { name = $"PiloSync-{tag}-win-x64.zip", browser_download_url = "https://example.test/zip", size = 1 }, new { name = complete ? "SHA256SUMS.txt" : "missing", browser_download_url = "https://example.test/sums", size = 1 } }
        };
        string json = JsonSerializer.Serialize(new[] { Release("v2027.0101a"), Release("v2027.0101z", draft: true), Release("v2027.0101y", prerelease: true), Release("v2027.0101x", complete: false), Release("invalid"), Release("v2026.1231z") });
        Assert.Equal("v2027.0101a", ReleaseSelection.Select(json, "v2026.1231a")?.Tag);
        Assert.Null(ReleaseSelection.Select(json, "v2027.0101a"));
        Assert.Null(ReleaseSelection.Select(json, "v2027.0101b"));
    }
}
