using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PiloSync.Core.Tests;

public class SourceEncodingTests
{
    [Fact]
    public void SourceFilesDoNotContainMojibake()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "PalmSync", "src"))
            && !(File.Exists(Path.Combine(directory.FullName, "PiloSync.sln")) && Directory.Exists(Path.Combine(directory.FullName, "src"))))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var source = Directory.Exists(Path.Combine(directory.FullName, "PalmSync", "src"))
            ? Path.Combine(directory.FullName, "PalmSync", "src") : Path.Combine(directory.FullName, "src");
        var utf8 = new UTF8Encoding(false, true);
        var tokens = new Regex("//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|'(?:\\\\.|[^'\\\\])*'|(?<literal>\\$?@\"(?:\"\"|[^\"])*\"|@\\$\"(?:\"\"|[^\"])*\"|\\$?\"(?:\\\\.|[^\"\\\\])*\")");
        var errors = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file, utf8); }
            catch (DecoderFallbackException) { errors.Add($"{file}: invalid UTF-8"); continue; }

            if (text.Contains('\uFFFD')) errors.Add($"{file}: U+FFFD");
            foreach (Match match in tokens.Matches(text))
                if (match.Groups["literal"].Success && match.Value.Contains("???"))
                    errors.Add($"{file}:{text.AsSpan(0, match.Index).Count('\n') + 1}: three consecutive question marks in a string");
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
