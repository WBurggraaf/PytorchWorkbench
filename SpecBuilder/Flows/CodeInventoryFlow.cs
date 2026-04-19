using System.Text;

namespace SpecBuilder.Flows;

internal sealed class CodeInventoryFlow : IPipelineFlow
{
    private static readonly string[] DefaultExtensions =
    [
        ".cs", ".fs", ".vb", ".xaml",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".inl", ".cu",
        ".py", ".pyi", ".js", ".ts", ".tsx", ".jsx",
        ".json", ".yml", ".yaml", ".xml", ".props", ".targets", ".csproj", ".sln",
        ".md", ".txt", ".cmd", ".bat", ".ps1", ".sh", ".cmake", ".toml", ".lock"
    ];

    private readonly string _workspaceRoot;
    private readonly string _originRoot;
    private readonly string[] _pathIgnorePaths;
    private readonly string _generatedRoot;
    private readonly string _inventoryRoot;
    private readonly string _byExtensionRoot;
    private readonly string _indexPath;

    public CodeInventoryFlow(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _originRoot = Path.Combine(workspaceRoot, "origin");
        _pathIgnorePaths =
        [
            Path.Combine(workspaceRoot, "pathignore.md"),
            Path.Combine(workspaceRoot, "SpecBuilder", "pathignore.md"),
            Path.Combine(workspaceRoot, "origin", "pathignore.md"),
        ];
        _generatedRoot = Path.Combine(workspaceRoot, "generated");
        _inventoryRoot = Path.Combine(_generatedRoot, "inventory");
        _byExtensionRoot = Path.Combine(_inventoryRoot, "by-extension");
        _indexPath = Path.Combine(_generatedRoot, "originfileindex.md");
    }

    public string Name => "1. Build code inventory markdown";

    public string Description =>
        "Scans the origin folder for code and config files, then writes a reusable markdown inventory.";

    public Task<FlowResult> ExecuteAsync()
    {
        if (!Directory.Exists(_originRoot))
        {
            return Task.FromResult(new FlowResult(
                $"Origin folder not found: {_originRoot}",
                _indexPath));
        }

        var ignorePatterns = LoadIgnorePatterns(out var ignoreSource);
        var files = Directory
            .EnumerateFiles(_originRoot, "*", SearchOption.AllDirectories)
            .Where(IsInterestingFile)
            .Where(path => !ShouldIgnore(path, ignorePatterns))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var grouped = files
            .GroupBy(GetExtensionKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ignorePatterns.Count > 0)
        {
            var ignoredExamples = Directory
                .EnumerateFiles(_originRoot, "*", SearchOption.AllDirectories)
                .Where(IsInterestingFile)
                .Where(path => ShouldIgnore(path, ignorePatterns))
                .Take(5)
                .Select(path => Path.GetRelativePath(_originRoot, path).Replace('\\', '/'))
                .ToList();

            Console.WriteLine($"Ignore file: {(ignoreSource ?? "(none)")}");
            Console.WriteLine($"Ignore patterns: {ignorePatterns.Count}");
            Console.WriteLine($"Ignored files: {ignoredExamples.Count}");
            foreach (var sample in ignoredExamples)
            {
                Console.WriteLine($"- ignored: {sample}");
            }
            Console.WriteLine();
        }

        Directory.CreateDirectory(_byExtensionRoot);

        var manifest = WriteExtensionFiles(grouped);
        File.WriteAllText(_indexPath, BuildIndexMarkdown(files, manifest), Encoding.UTF8);

        return Task.FromResult(new FlowResult(
            $"Wrote inventory for {files.Count} files across {grouped.Count} extension groups.",
            _indexPath));
    }

    private bool IsInterestingFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = GetExtensionKey(path);
        return DefaultExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> LoadIgnorePatterns(out string? ignoreSource)
    {
        var ignorePath = _pathIgnorePaths.FirstOrDefault(File.Exists);
        ignoreSource = ignorePath;
        if (ignorePath is null)
        {
            return [];
        }

        return File.ReadAllLines(ignorePath, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Trim('*', ' ', '\t'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static bool ShouldIgnore(string path, IReadOnlyList<string> ignorePatterns)
    {
        if (ignorePatterns.Count == 0)
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        foreach (var pattern in ignorePatterns)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            if (normalizedPath.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetExtensionKey(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return ".csproj";
        }

        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return ".sln";
        }

        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "[no extension]" : ext.ToLowerInvariant();
    }

    private IReadOnlyList<ExtensionManifest> WriteExtensionFiles(List<IGrouping<string, string>> grouped)
    {
        var manifests = new List<ExtensionManifest>();

        foreach (var group in grouped)
        {
            var fileName = SanitizeFileName(group.Key) + ".md";
            var outputPath = Path.Combine(_byExtensionRoot, fileName);
            var relativePaths = group
                .Select(path => Path.GetRelativePath(_originRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            File.WriteAllText(outputPath, BuildExtensionMarkdown(group.Key, relativePaths), Encoding.UTF8);

            manifests.Add(new ExtensionManifest(
                group.Key,
                relativePaths.Count,
                Path.GetRelativePath(_inventoryRoot, outputPath).Replace('\\', '/'),
                relativePaths));
        }

        return manifests;
    }

    private string BuildIndexMarkdown(List<string> files, IReadOnlyList<ExtensionManifest> manifests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Code Inventory");
        sb.AppendLine();
        sb.AppendLine($"- Workspace: `{_workspaceRoot}`");
        sb.AppendLine($"- Origin: `{_originRoot}`");
        sb.AppendLine($"- Generated: `{DateTimeOffset.Now:O}`");
        sb.AppendLine($"- File count: `{files.Count}`");
        sb.AppendLine($"- Inventory root: `{Path.Combine(_inventoryRoot)}`");
        sb.AppendLine();
        sb.AppendLine("## Reading Order");
        sb.AppendLine();
        sb.AppendLine("1. Start with `by-extension/*.md` to inspect grouped files.");
        sb.AppendLine("2. Use the table below to jump to specific extensions.");
        sb.AppendLine();
        sb.AppendLine("## Extension Summary");
        sb.AppendLine();
        sb.AppendLine("| Extension | Count | Markdown |");
        sb.AppendLine("| --- | ---: | --- |");

        foreach (var item in manifests.OrderBy(item => item.Extension, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{item.Extension}` | {item.Count} | `{item.RelativeMarkdownPath}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Extension Files");
        sb.AppendLine();

        foreach (var item in manifests.OrderBy(item => item.Extension, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### `{item.Extension}`");
            sb.AppendLine();
            sb.AppendLine($"- Markdown: `{item.RelativeMarkdownPath}`");
            sb.AppendLine($"- Count: `{item.Count}`");
            sb.AppendLine();
            foreach (var file in item.RelativeFiles)
            {
                sb.AppendLine($"- `{file}`");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildExtensionMarkdown(string extension, List<string> relativePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# `{extension}` Files");
        sb.AppendLine();
        sb.AppendLine($"- File count: `{relativePaths.Count}`");
        sb.AppendLine();
        sb.AppendLine("## Files");
        sb.AppendLine();

        foreach (var file in relativePaths)
        {
            sb.AppendLine($"- `{file}`");
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        var chars = value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned.Trim('_');
    }

    private sealed record ExtensionManifest(
        string Extension,
        int Count,
        string RelativeMarkdownPath,
        IReadOnlyList<string> RelativeFiles);
}
