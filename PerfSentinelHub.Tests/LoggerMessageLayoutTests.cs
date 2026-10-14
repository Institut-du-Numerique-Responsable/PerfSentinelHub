using System.Text.RegularExpressions;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The logging source generator emits one block per class declaration, in an order that
///     changes from one compilation to the next. Two blocks of one class then initialize its
///     static fields in either order, and the native build stops being reproducible byte for
///     byte, which the release workflow refuses. One file per class keeps one block.
/// </summary>
public sealed partial class LoggerMessageLayoutTests
{
    [Fact]
    public void Every_class_declares_its_log_messages_in_one_file()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PerfSentinelHub.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var sources = Path.Combine(directory.FullName, "PerfSentinelHub");

        var filesByClass = new Dictionary<string, SortedSet<string>>();
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.StartsWith("obj", StringComparison.Ordinal) ||
                relative.StartsWith("bin", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            var classes = ClassDeclaration().Matches(text);
            foreach (Match attribute in LoggerMessageAttribute().Matches(text))
            {
                var owner = classes.LastOrDefault(declaration => declaration.Index < attribute.Index);
                Assert.NotNull(owner);
                var name = owner.Groups[1].Value;
                if (!filesByClass.TryGetValue(name, out var files))
                    filesByClass[name] = files = new SortedSet<string>(StringComparer.Ordinal);
                files.Add(relative);
            }
        }

        Assert.NotEmpty(filesByClass);
        Assert.All(filesByClass, entry => Assert.True(entry.Value.Count == 1,
            $"{entry.Key} declares [LoggerMessage] methods in {string.Join(", ", entry.Value)}"));
    }

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\[LoggerMessage\b")]
    private static partial Regex LoggerMessageAttribute();
}
