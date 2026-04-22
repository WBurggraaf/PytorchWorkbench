using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CppAst;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SpecBuilder.Flows;

internal sealed class OllamaExtensionAnalysisFlow : IPipelineFlow
{
    private readonly string _workspaceRoot;
    private readonly string _originRoot;
    private readonly string _indexPath;
    private readonly string _outputRoot;
    private readonly string _cachePath;

    public OllamaExtensionAnalysisFlow(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _originRoot = Path.Combine(workspaceRoot, "origin");
        _indexPath = Path.Combine(workspaceRoot, "generated", "originfileindex.md");
        _outputRoot = Path.Combine(workspaceRoot, "generated", "ast-database");
        _cachePath = Path.Combine(_outputRoot, "parse-cache.json");
    }

    public string Name => "3. Build in-memory AST database";

    public string Description =>
        "Reads generated/originfileindex.md, lets you choose extension groups with real parsers, and builds an in-memory AST database for the selected code files.";

    public async Task<FlowResult> ExecuteAsync()
    {
        try
        {
            Console.WriteLine("[step3] starting");
            if (!File.Exists(_indexPath))
            {
                Console.WriteLine($"[step3] missing index: {_indexPath}");
                return new FlowResult($"Missing origin file index: {_indexPath}. Run step 1 first.", _indexPath);
            }

            Console.WriteLine("[step3] reading origin file index");
            var indexMarkdown = await File.ReadAllTextAsync(_indexPath, Encoding.UTF8);
            Console.WriteLine("[step3] parsing groups");
            var groups = ParseIndex(indexMarkdown)
                .GroupBy(group => GetParserGroupLabel(group.Extension), StringComparer.OrdinalIgnoreCase)
                .Where(group => !group.Key.Equals("Generic", StringComparison.OrdinalIgnoreCase))
                .Select(group => new ParserGroup(group.Key, group.SelectMany(x => x.Files).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), group.Select(x => x.Extension).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groups.Count == 0)
            {
                Console.WriteLine("[step3] no supported groups found");
                return new FlowResult("No AST-supported extension groups found in originfileindex.md.", _indexPath);
            }

            Console.WriteLine("[step3] selecting groups");
            var selectedGroups = SelectGroups(groups);
            if (selectedGroups.Count == 0)
            {
                Console.WriteLine("[step3] no groups selected");
                return new FlowResult("No extension groups selected.", _outputRoot);
            }

            Directory.CreateDirectory(_outputRoot);
            ClearStep3Outputs();

            Console.WriteLine("[step3] loading cache");
            var cache = AstParseCache.Load(_cachePath);
            var database = new AstDatabase(_workspaceRoot, _originRoot);
            foreach (var group in selectedGroups)
            {
                Console.WriteLine($"[step3] indexing `{group.Label}` ({group.Files.Count} files)");
                var maxConcurrency = Math.Max(1, (int)Math.Ceiling(Environment.ProcessorCount * 0.75));
                var summary = await ProcessGroupAsync(group, maxConcurrency, cache, database.Add);
                Console.WriteLine($"[step3] finished `{group.Label}`: parsed={summary.Parsed}, missing={summary.Missing}, failed={summary.Failed}");
            }

            var snapshotPath = Path.Combine(_outputRoot, $"ast-database-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
        Console.WriteLine($"[step3] serializing snapshot to {snapshotPath}");
        await database.WriteJsonAsync(snapshotPath);
        Console.WriteLine("[step3] saving cache");
        cache.Save(_cachePath);

            var summaryPath = Path.Combine(_outputRoot, "index.md");
            Console.WriteLine("[step3] writing summary and query guide");
            await File.WriteAllTextAsync(summaryPath, BuildSummaryMarkdown(database, snapshotPath), Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(_outputRoot, "queries.md"), BuildQueryGuideMarkdown(database), Encoding.UTF8);

            Console.WriteLine("[step3] complete");
            return new FlowResult(
                $"Built in-memory AST database for {database.Files.Count} files across {selectedGroups.Count} language groups.",
                summaryPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[step3] failed");
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    private void ClearStep3Outputs()
    {
        if (Directory.Exists(_outputRoot))
        {
            foreach (var file in Directory.GetFiles(_outputRoot, "ast-database-*.json"))
            {
                File.Delete(file);
            }

            foreach (var file in Directory.GetFiles(_outputRoot, "*.md"))
            {
                File.Delete(file);
            }

            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
    }

    private static bool TryParseFile(string fullPath, string groupLabel, out AstFileRecord record, out string error)
    {
        var relativePath = fullPath;
        var ext = Path.GetExtension(fullPath).Trim().TrimStart('.').ToLowerInvariant();
        var content = File.ReadAllText(fullPath, Encoding.UTF8);

        if (TryGetParserKind(ext, out var parserKind))
        {
            switch (parserKind)
            {
                case ParserKind.CSharp:
                    record = ParseCSharp(relativePath, content);
                    error = string.Empty;
                    return true;
                case ParserKind.Cpp:
                    try
                    {
                        record = ParseCpp(relativePath, fullPath, content);
                        error = string.Empty;
                        return true;
                    }
                    catch (DllNotFoundException ex) when (ex.Message.Contains("libclang", StringComparison.OrdinalIgnoreCase))
                    {
                        record = ParseGeneric(relativePath, ext, content);
                        error = "libclang unavailable, used generic structural parse";
                        return true;
                    }
                case ParserKind.Python:
                    var parsed = ParsePython(relativePath, fullPath, content, out error);
                    if (parsed is not null)
                    {
                        record = parsed;
                        return true;
                    }

                    break;
            }
        }

        record = ParseGeneric(relativePath, ext, content);
        error = "generic structural parse";
        return true;
    }

    private async Task<GroupParseSummary> ProcessGroupAsync(ParserGroup group, int maxConcurrency, AstParseCache cache, Action<AstFileRecord> addRecord)
    {
        var summary = new GroupParseSummary();
        for (var start = 0; start < group.Files.Count; start += maxConcurrency)
        {
            var batch = group.Files
                .Skip(start)
                .Take(maxConcurrency)
                .Select((relativePath, offset) =>
                {
                    var fullPath = Path.Combine(_originRoot, relativePath);
                    return ProcessFileAsync(group.Label, relativePath, fullPath, start + offset + 1, group.Files.Count, cache);
                })
                .ToList();

            var results = await Task.WhenAll(batch);
            foreach (var item in results.OrderBy(item => item.Index))
            {
                addRecord(item.Record);
                summary.Add(item.Status);
            }
        }

        return summary;
    }

    private static async Task<GroupParseResult> ProcessFileAsync(string groupLabel, string relativePath, string fullPath, int index, int total, AstParseCache cache)
    {
        if (index == 1 || index % 25 == 0 || index == total)
        {
            Console.WriteLine($"[{groupLabel}] {index}/{total} {relativePath}");
        }

        if (!File.Exists(fullPath))
        {
            return new GroupParseResult(index, "missing", new AstFileRecord(relativePath, Path.GetExtension(relativePath), groupLabel, new AstNode("MissingFile", Path.GetFileName(relativePath)), "missing"));
        }

        var cacheKey = BuildCacheKey(relativePath, groupLabel, fullPath);
        if (cache.TryGet(cacheKey, out var cached))
        {
            return new GroupParseResult(index, "cached", cached);
        }

        if (TryParseFile(fullPath, groupLabel, out var record, out var error))
        {
            cache.Set(cacheKey, record);
            return new GroupParseResult(index, record.Status, record);
        }

        return new GroupParseResult(index, "error", new AstFileRecord(relativePath, Path.GetExtension(relativePath), groupLabel, new AstNode("ParseError", error), "error"));
    }

    private static string BuildCacheKey(string relativePath, string groupLabel, string fullPath)
    {
        var hash = ComputeFileHash(fullPath);
        var normalizedPath = Path.GetFullPath(fullPath).Replace('\\', '/').ToLowerInvariant();
        return $"{groupLabel}|{normalizedPath}|{hash}";
    }

    private static string ComputeFileHash(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static AstFileRecord ParseCSharp(string filePath, string content)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(content, new CSharpParseOptions(LanguageVersion.Latest));
        var root = ConvertRoslynNode(syntaxTree.GetRoot(), "CompilationUnit");
        ExtractCSharpReferences(syntaxTree.GetRoot(), root);
        return new AstFileRecord(filePath, ".cs", "C#", root, "parsed");
    }

    private static AstFileRecord ParseCpp(string relativePath, string fullPath, string content)
    {
        var compilation = CppParser.Parse(content);
        var root = new AstNode("TranslationUnit", Path.GetFileName(fullPath));
        foreach (var diagnostic in compilation.Diagnostics.Messages)
        {
            root.Children.Add(new AstNode("Diagnostic", diagnostic.ToString()));
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                root.References.Add(new AstReference("include", ExtractDelimited(trimmed), null, trimmed));
            }
        }

        foreach (var cppClass in compilation.Classes)
        {
            var node = new AstNode("Class", cppClass.ToString());
            node.References.Add(new AstReference("defines", cppClass.Name, null, cppClass.ToString()));
            root.Children.Add(node);
        }

        foreach (var cppEnum in compilation.Enums)
        {
            var node = new AstNode("Enum", cppEnum.ToString());
            node.References.Add(new AstReference("defines", cppEnum.Name, null, cppEnum.ToString()));
            root.Children.Add(node);
        }

        foreach (var cppFunction in compilation.Functions)
        {
            var node = new AstNode("Function", cppFunction.ToString());
            node.References.Add(new AstReference("defines", cppFunction.Name, null, cppFunction.ToString()));
            root.Children.Add(node);
        }

        foreach (var typedef in compilation.Typedefs)
        {
            var node = new AstNode("Typedef", typedef.ToString());
            node.References.Add(new AstReference("defines", typedef.Name, null, typedef.ToString()));
            root.Children.Add(node);
        }

        return new AstFileRecord(relativePath, GetExtensionKey(fullPath), "C/C++", root, "parsed");
    }

    private static AstFileRecord? ParsePython(string relativePath, string fullPath, string content, out string error)
    {
        var script = """
import ast
import json
import sys

path = sys.argv[1]
source = sys.stdin.read()

def convert(node):
    result = {
        "kind": type(node).__name__,
        "name": getattr(node, "name", None),
        "lineno": getattr(node, "lineno", None),
        "children": []
    }
    for child in ast.iter_child_nodes(node):
        result["children"].append(convert(child))
    return result

tree = ast.parse(source, filename=path)
print(json.dumps(convert(tree)))
""";

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            ArgumentList = { "-c", script, fullPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            error = "Unable to start python.";
            return null;
        }

        process.StandardInput.Write(content);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(stderr) ? "Python parser failed." : stderr.Trim();
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = ConvertJsonAst(doc.RootElement);
        ExtractPythonReferences(content, root);
        error = string.Empty;
        return new AstFileRecord(relativePath, ".py", "Python", root, "parsed");
    }

    private static AstNode ConvertJsonAst(JsonElement element)
    {
        var kind = element.GetProperty("kind").GetString() ?? "Unknown";
        var name = element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var node = new AstNode(kind, name);

        if (element.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                node.Children.Add(ConvertJsonAst(child));
            }
        }

        if (kind is "Import" or "ImportFrom" or "FunctionDef" or "AsyncFunctionDef" or "ClassDef")
        {
            node.References.Add(new AstReference("defines", name, null, kind));
        }

        return node;
    }

    private static AstNode ConvertRoslynNode(SyntaxNode node, string? label = null)
    {
        var astNode = new AstNode(node.Kind().ToString(), label ?? node.Kind().ToString(), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        foreach (var child in node.ChildNodes())
        {
            astNode.Children.Add(ConvertRoslynNode(child));
        }

        return astNode;
    }

    private static void ExtractCSharpReferences(SyntaxNode node, AstNode root)
    {
        foreach (var declaration in node.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", declaration.Identifier.ValueText, null, declaration.ToString()));
        }

        foreach (var method in node.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", method.Identifier.ValueText, null, method.ToString()));
        }

        foreach (var ctor in node.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", ctor.Identifier.ValueText, null, ctor.ToString()));
        }

        foreach (var dtor in node.DescendantNodes().OfType<DestructorDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", dtor.Identifier.ValueText, null, dtor.ToString()));
        }

        foreach (var op in node.DescendantNodes().OfType<OperatorDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", op.OperatorToken.ValueText, null, op.ToString()));
        }

        foreach (var conv in node.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>())
        {
            root.References.Add(new AstReference("defines", conv.Type.ToString(), null, conv.ToString()));
        }

        foreach (var usingDirective in node.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var target = usingDirective.Name?.ToString();
            if (!string.IsNullOrWhiteSpace(target))
            {
                root.References.Add(new AstReference("using", target, null, usingDirective.ToString()));
            }
        }

        foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            root.References.Add(new AstReference("call", invocation.Expression.ToString(), null, invocation.ToString()));
        }

        foreach (var declaration in node.DescendantNodes().OfType<BaseTypeSyntax>())
        {
            root.References.Add(new AstReference("type", declaration.Type.ToString(), null, declaration.ToString()));
        }
    }

    private static void ExtractPythonReferences(string content, AstNode root)
    {
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                root.References.Add(new AstReference("import", ExtractNameAfterKeyword(trimmed), null, trimmed));
            }
        }

        foreach (var node in root.Flatten())
        {
            if (node.Kind is "FunctionDef" or "AsyncFunctionDef" or "ClassDef")
            {
                root.References.Add(new AstReference("defines", node.Name, null, node.Kind));
            }
        }
    }

    private static string? ExtractDelimited(string line)
    {
        var start = line.IndexOf('"');
        if (start >= 0)
        {
            var end = line.IndexOf('"', start + 1);
            if (end > start)
            {
                return line[(start + 1)..end];
            }
        }

        start = line.IndexOf('<');
        if (start >= 0)
        {
            var end = line.IndexOf('>', start + 1);
            if (end > start)
            {
                return line[(start + 1)..end];
            }
        }

        return null;
    }

    private static IReadOnlyList<ExtensionGroup> ParseIndex(string indexMarkdown)
    {
        var groups = new List<ExtensionGroup>();
        var current = default(ExtensionGroupBuilder);
        var inExtensionFiles = false;

        foreach (var rawLine in indexMarkdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Equals("## Extension Files", StringComparison.OrdinalIgnoreCase))
            {
                inExtensionFiles = true;
                continue;
            }

            if (!inExtensionFiles)
            {
                continue;
            }

            if (trimmed.StartsWith("### `", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    groups.Add(current.Build());
                }

                var extension = trimmed.TrimStart('#', ' ').Trim('`');
                current = new ExtensionGroupBuilder(extension);
                continue;
            }

            if (current is null || !trimmed.StartsWith("- `", StringComparison.Ordinal))
            {
                continue;
            }

            var file = trimmed.TrimStart('-', ' ').Trim('`').Trim();
            if (!string.IsNullOrWhiteSpace(file))
            {
                current.Files.Add(file);
            }
        }

        if (current is not null)
        {
            groups.Add(current.Build());
        }

        return groups;
    }

    private static IReadOnlyList<ParserGroup> SelectGroups(IReadOnlyList<ParserGroup> groups)
    {
        while (true)
        {
            Console.WriteLine("Select extension groups to index as ASTs:");
            Console.WriteLine("A. All groups");
            for (var i = 0; i < groups.Count; i++)
            {
                Console.WriteLine($"{i + 1}. `{groups[i].Label}` ({groups[i].Files.Count} files, {string.Join(", ", groups[i].Extensions.Select(x => $"`{x}`"))})");
            }

            Console.WriteLine();
            Console.Write("Choose a group number, a comma list, a range like 2-5, or A: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                return groups;
            }

            var selected = ParseSelection(input, groups);
            if (selected.Count > 0)
            {
                return selected;
            }

            Console.WriteLine("Invalid selection. Press Enter to try again.");
            Console.ReadLine();
            Console.WriteLine();
        }
    }

    private static IReadOnlyList<ParserGroup> ParseSelection(string input, IReadOnlyList<ParserGroup> groups)
    {
        var indexes = new SortedSet<int>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (bounds.Length == 2 && int.TryParse(bounds[0], out var start) && int.TryParse(bounds[1], out var end))
                {
                    if (start > end)
                    {
                        (start, end) = (end, start);
                    }

                    for (var i = start; i <= end; i++)
                    {
                        if (i >= 1 && i <= groups.Count)
                        {
                            indexes.Add(i - 1);
                        }
                    }
                }
            }
            else if (int.TryParse(part, out var index) && index >= 1 && index <= groups.Count)
            {
                indexes.Add(index - 1);
            }
        }

        return indexes.Select(i => groups[i]).ToList();
    }

    private static bool TryGetParserKind(string extension, out ParserKind kind)
    {
        kind = default;
        var ext = extension.Trim().TrimStart('.').ToLowerInvariant();
        switch (ext)
        {
            case "cs":
                kind = ParserKind.CSharp;
                return true;
            case "c":
            case "cc":
            case "cpp":
            case "cxx":
            case "h":
            case "hh":
            case "hpp":
            case "inl":
                kind = ParserKind.Cpp;
                return true;
            case "py":
                kind = ParserKind.Python;
                return true;
            default:
                return false;
        }
    }

    private static string GetParserGroupLabel(string extension)
    {
        return TryGetParserKind(extension, out var kind) ? kind switch
        {
            ParserKind.CSharp => "C#",
            ParserKind.Cpp => "C/C++",
            ParserKind.Python => "Python",
            _ => "Generic",
        } : "Generic";
    }

    private static string BuildSummaryMarkdown(AstDatabase database, string snapshotPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AST Database");
        sb.AppendLine();
        sb.AppendLine($"- Workspace: `{database.WorkspaceRoot}`");
        sb.AppendLine($"- Origin: `{database.OriginRoot}`");
        sb.AppendLine($"- Files indexed: `{database.Files.Count}`");
        sb.AppendLine($"- Language groups: `{database.Groups.Count}`");
        sb.AppendLine($"- Snapshot: `{snapshotPath}`");
        sb.AppendLine();
        sb.AppendLine("## Indexed Files");
        sb.AppendLine();
        sb.AppendLine("| File | Extension | Language | Root nodes | Status |");
        sb.AppendLine("| --- | --- | --- | ---: | --- |");

        foreach (var file in database.Files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{file.RelativePath}` | `{file.Extension}` | `{file.Language}` | {file.RootNodeCount} | `{file.Status}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Groups");
        sb.AppendLine();
        foreach (var group in database.Groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### `{group.Key}`");
            sb.AppendLine();
            sb.AppendLine($"- Files: `{group.Value.FileCount}`");
            sb.AppendLine($"- Root nodes: `{group.Value.TotalRootNodes}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildQueryGuideMarkdown(AstDatabase database)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AST Query Guide");
        sb.AppendLine();
        sb.AppendLine("Use the in-memory database query helpers to filter by file, extension, language, kind, or name.");
        sb.AppendLine();
        sb.AppendLine("## Examples");
        sb.AppendLine();
        sb.AppendLine("- `database.QueryFiles()`");
        sb.AppendLine("- `database.QueryNodes(kind: \"Function\")`");
        sb.AppendLine("- `database.QueryNodes(language: \"Python\", nameContains: \"test\")`");
        sb.AppendLine("- `database.QueryNodes(extension: \".cs\", recursive: true)`");
        sb.AppendLine();
        sb.AppendLine("## Available Kinds");
        sb.AppendLine();

        foreach (var kind in database.KnownKinds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"- `{kind}`");
        }

        return sb.ToString();
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

    private enum ParserKind
    {
        CSharp,
        Cpp,
        Python,
    }

    private sealed record ParserGroup(string Label, IReadOnlyList<string> Files, IReadOnlyList<string> Extensions);

    private sealed record GroupParseResult(int Index, string Status, AstFileRecord Record);

    private sealed class GroupParseSummary
    {
        public int Parsed { get; private set; }
        public int Missing { get; private set; }
        public int Failed { get; private set; }

        public void Add(string status)
        {
            if (status == "missing")
            {
                Missing++;
            }
            else if (status == "error")
            {
                Failed++;
            }
            else
            {
                Parsed++;
            }
        }
    }

    private sealed class AstParseCache
    {
        private readonly Dictionary<string, AstFileRecord> _entries = new(StringComparer.OrdinalIgnoreCase);

        public static AstParseCache Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new AstParseCache();
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var entries = JsonSerializer.Deserialize<Dictionary<string, AstFileRecord>>(json);
                var cache = new AstParseCache();
                if (entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        cache._entries[entry.Key] = entry.Value;
                    }
                }

                return cache;
            }
            catch
            {
                return new AstParseCache();
            }
        }

        public bool TryGet(string key, out AstFileRecord record) => _entries.TryGetValue(key, out record!);

        public void Set(string key, AstFileRecord record) => _entries[key] = record;

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var entry in _entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(entry.Key);
                AstDatabase.WriteFile(writer, entry.Value);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
    }

    private sealed record ExtensionGroup(string Extension, IReadOnlyList<string> Files);

    private sealed class ExtensionGroupBuilder
    {
        public ExtensionGroupBuilder(string extension)
        {
            Extension = extension;
        }

        public string Extension { get; }
        public List<string> Files { get; } = [];

        public ExtensionGroup Build() => new(Extension, Files.ToList());
    }

    private sealed class AstDatabase
    {
        private readonly string _workspaceRoot;
        private readonly string _originRoot;
        private readonly List<AstFileRecord> _files = [];
        private readonly Dictionary<string, AstGroupRecord> _groups = new(StringComparer.OrdinalIgnoreCase);

        public AstDatabase(string workspaceRoot, string originRoot)
        {
            _workspaceRoot = workspaceRoot;
            _originRoot = originRoot;
        }

        public string WorkspaceRoot => _workspaceRoot;
        public string OriginRoot => _originRoot;
        public IReadOnlyList<AstFileRecord> Files => _files;
        public IReadOnlyDictionary<string, AstGroupRecord> Groups => _groups;
        public IReadOnlyCollection<string> KnownKinds => _files.SelectMany(file => file.Root.Flatten()).Select(node => node.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public void Add(AstFileRecord file)
        {
            _files.Add(file);
            if (!_groups.TryGetValue(file.Extension, out var group))
            {
                group = new AstGroupRecord(file.Extension);
                _groups[file.Extension] = group;
            }

            group.Add(file);
        }

        public IReadOnlyList<AstFileRecord> QueryFiles(string? extension = null, string? language = null, string? status = null, string? pathContains = null)
        {
            IEnumerable<AstFileRecord> query = _files;

            if (!string.IsNullOrWhiteSpace(extension))
            {
                query = query.Where(file => file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                query = query.Where(file => file.Language.Equals(language, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(file => file.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(pathContains))
            {
                query = query.Where(file => file.RelativePath.Contains(pathContains, StringComparison.OrdinalIgnoreCase));
            }

            return query.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<AstNodeQueryResult> QueryNodes(
            string? kind = null,
            string? nameContains = null,
            string? extension = null,
            string? language = null,
            string? pathContains = null,
            bool recursive = true)
        {
            var files = QueryFiles(extension, language, null, pathContains);
            var results = new List<AstNodeQueryResult>();

            foreach (var file in files)
            {
                var nodes = recursive ? file.Root.Flatten() : file.Root.Children.AsEnumerable();
                foreach (var node in nodes)
                {
                    if (!string.IsNullOrWhiteSpace(kind) && !node.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(nameContains) &&
                        (node.Name is null || !node.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    results.Add(new AstNodeQueryResult(file.RelativePath, file.Extension, file.Language, node));
                }
            }

            return results;
        }

        public void AddMissing(string relativePath, string extension)
        {
            Add(new AstFileRecord(relativePath, extension, "Missing", new AstNode("MissingFile", Path.GetFileName(relativePath)), "missing"));
        }

        public void AddError(string relativePath, string extension, string error)
        {
            Add(new AstFileRecord(relativePath, extension, "ParseError", new AstNode("ParseError", error), "error"));
        }

        public async Task WriteJsonAsync(string snapshotPath)
        {
            await using var stream = File.Create(snapshotPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteString("WorkspaceRoot", _workspaceRoot);
            writer.WriteString("OriginRoot", _originRoot);
            writer.WriteString("GeneratedAt", DateTimeOffset.UtcNow);
            writer.WriteString("SnapshotPath", snapshotPath);

            writer.WritePropertyName("Files");
            writer.WriteStartArray();
            foreach (var file in _files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                WriteFile(writer, file);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("Groups");
            writer.WriteStartArray();
            foreach (var group in _groups.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                WriteGroup(writer, group);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("Symbols");
            writer.WriteStartArray();
            foreach (var symbol in BuildSymbols().OrderBy(symbol => symbol.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("Name", symbol.Name);
                writer.WriteString("Kind", symbol.Kind);
                writer.WriteString("Category", symbol.Category);
                writer.WriteString("RelativePath", symbol.RelativePath);
                writer.WriteString("Language", symbol.Language);
                writer.WriteString("Evidence", symbol.Evidence);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync();
        }

        private IEnumerable<CanonicalAstSymbol> BuildSymbols()
        {
            foreach (var file in _files)
            {
                foreach (var reference in file.References)
                {
                    if (reference.Kind is not "defines" || string.IsNullOrWhiteSpace(reference.Target))
                    {
                        continue;
                    }

                    yield return new CanonicalAstSymbol(
                        reference.Target!,
                        reference.Kind,
                        "definition",
                        file.RelativePath,
                        file.Language,
                        reference.Evidence);
                }

                foreach (var node in file.Root.Flatten())
                {
                    if (string.IsNullOrWhiteSpace(node.Name))
                    {
                        continue;
                    }

                    if (node.References.Any(reference => reference.Kind == "defines"))
                    {
                        yield return new CanonicalAstSymbol(
                            node.Name!,
                            node.Kind,
                            "definition",
                            file.RelativePath,
                            file.Language,
                            node.Summary ?? node.Signature ?? node.Kind);
                    }
                }
            }
        }

        private static void WriteGroup(Utf8JsonWriter writer, AstGroupRecord group)
        {
            writer.WriteStartObject();
            writer.WriteString("Key", group.Key);
            writer.WriteNumber("FileCount", group.FileCount);
            writer.WriteNumber("TotalRootNodes", group.TotalRootNodes);
            writer.WritePropertyName("Extensions");
            writer.WriteStartArray();
            foreach (var extension in group.Extensions)
            {
                writer.WriteStringValue(extension);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        internal static void WriteFile(Utf8JsonWriter writer, AstFileRecord file)
        {
            writer.WriteStartObject();
            writer.WriteString("RelativePath", file.RelativePath);
            writer.WriteString("Extension", file.Extension);
            writer.WriteString("Language", file.Language);
            writer.WriteString("Status", file.Status);
            writer.WriteNumber("RootNodeCount", file.RootNodeCount);
            writer.WritePropertyName("Root");
            WriteNode(writer, file.Root, 0);
            writer.WritePropertyName("Tags");
            writer.WriteStartArray();
            foreach (var tag in file.Tags)
            {
                writer.WriteStringValue(tag);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("References");
            writer.WriteStartArray();
            foreach (var reference in file.References)
            {
                WriteReference(writer, reference);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteNode(Utf8JsonWriter writer, AstNode node, int depth)
        {
            writer.WriteStartObject();
            writer.WriteString("Kind", node.Kind);
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                writer.WriteString("Name", node.Name);
            }

            if (node.Line > 0)
            {
                writer.WriteNumber("Line", node.Line);
            }

            if (!string.IsNullOrWhiteSpace(node.Signature))
            {
                writer.WriteString("Signature", node.Signature);
            }

            if (!string.IsNullOrWhiteSpace(node.Summary))
            {
                writer.WriteString("Summary", node.Summary);
            }

            writer.WritePropertyName("References");
            writer.WriteStartArray();
            foreach (var reference in node.References)
            {
                WriteReference(writer, reference);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("Children");
            writer.WriteStartArray();
            var childLimit = depth >= 4 ? 0 : 32;
            var count = 0;
            foreach (var child in node.Children)
            {
                if (count++ >= childLimit)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Kind", "Truncated");
                    writer.WriteString("Name", "ChildLimit");
                    writer.WriteEndObject();
                    break;
                }

                WriteNode(writer, child, depth + 1);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteReference(Utf8JsonWriter writer, AstReference reference)
        {
            writer.WriteStartObject();
            writer.WriteString("Kind", reference.Kind);
            if (!string.IsNullOrWhiteSpace(reference.Target))
            {
                writer.WriteString("Target", reference.Target);
            }

            if (!string.IsNullOrWhiteSpace(reference.TargetPath))
            {
                writer.WriteString("TargetPath", reference.TargetPath);
            }

            if (!string.IsNullOrWhiteSpace(reference.Evidence))
            {
                writer.WriteString("Evidence", reference.Evidence);
            }

            writer.WriteEndObject();
        }
    }

    internal sealed class AstGroupRecord
    {
        private readonly List<AstFileRecord> _files = [];

        public AstGroupRecord(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public int FileCount => _files.Count;
        public int TotalRootNodes => _files.Sum(file => file.RootNodeCount);
        public IReadOnlyList<string> Extensions => _files.Select(file => file.Extension).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        public void Add(AstFileRecord file) => _files.Add(file);
    }

    internal sealed class AstFileRecord
    {
        public AstFileRecord(string relativePath, string extension, string language, AstNode root, string status)
        {
            RelativePath = relativePath.Replace('\\', '/');
            Extension = extension;
            Language = language;
            Root = root;
            RootNodeCount = root.Children.Count;
            Status = status;
        }

        public string RelativePath { get; }
        public string Extension { get; }
        public string Language { get; }
        public int RootNodeCount { get; }
        public string Status { get; }
        public AstNode Root { get; }
        public IReadOnlyList<string> Tags => Root.Children.Select(child => child.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public IReadOnlyList<AstReference> References => Root.Flatten().SelectMany(node => node.References).DistinctBy(reference => $"{reference.Kind}|{reference.Target}|{reference.TargetPath}|{reference.Evidence}", StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal sealed class AstNode
    {
        public AstNode(string kind, string? name = null, int line = 0)
        {
            Kind = kind;
            Name = name;
            Line = line;
        }

        public string Kind { get; }
        public string? Name { get; }
        public int Line { get; }
        public string? Signature { get; set; }
        public string? Summary { get; set; }
        public List<AstReference> References { get; } = [];
        public List<AstNode> Children { get; } = [];

        public IEnumerable<AstNode> Flatten()
        {
            yield return this;
            foreach (var child in Children)
            {
                foreach (var nested in child.Flatten())
                {
                    yield return nested;
                }
            }
        }
    }

    internal sealed record AstReference(string Kind, string? Target, string? TargetPath, string? Evidence);

    private sealed record AstNodeQueryResult(string RelativePath, string Extension, string Language, AstNode Node);

    private static AstFileRecord ParseGeneric(string relativePath, string extension, string content)
    {
        var root = new AstNode("Document", Path.GetFileName(relativePath));
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var currentBlock = (AstNode?)null;

        foreach (var (line, index) in lines.Select((value, i) => (value, i + 1)))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                currentBlock = null;
                continue;
            }

            var kind = ClassifyGenericLine(trimmed, out var name);
            var node = new AstNode(kind, name, index);
            if (kind is "Import" && name is not null)
            {
                node.References.Add(new AstReference("import", name, null, trimmed));
            }
            else if (kind is "Function" && name is not null)
            {
                node.References.Add(new AstReference("symbol", name, null, trimmed));
            }

            if (kind is "Class" or "Function" or "Namespace" or "Module" or "Import" or "Constant" or "Struct" or "Enum" or "Interface" or "Comment")
            {
                root.Children.Add(node);
                currentBlock = node;
                continue;
            }

            if (currentBlock is not null && IsLikelyBlockContent(trimmed))
            {
                currentBlock.Children.Add(node);
                continue;
            }

            root.Children.Add(node);
            currentBlock = node;
        }

        return new AstFileRecord(relativePath, extension, "Generic", root, "generic");
    }

    private static string ClassifyGenericLine(string line, out string? name)
    {
        name = null;

        if (line.StartsWith("//", StringComparison.Ordinal) ||
            line.StartsWith("/*", StringComparison.Ordinal) ||
            line.StartsWith("# ", StringComparison.Ordinal) ||
            line.StartsWith("<!--", StringComparison.Ordinal))
        {
            return "Comment";
        }

        if (line.StartsWith("using ", StringComparison.Ordinal) ||
            line.StartsWith("import ", StringComparison.Ordinal) ||
            line.StartsWith("from ", StringComparison.Ordinal) ||
            line.StartsWith("include ", StringComparison.Ordinal) ||
            line.StartsWith("#include", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Import";
        }

        if (line.StartsWith("namespace ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Namespace";
        }

        if (line.StartsWith("module ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Module";
        }

        if (line.StartsWith("class ", StringComparison.Ordinal) || line.Contains(" class ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Class";
        }

        if (line.StartsWith("interface ", StringComparison.Ordinal) || line.Contains(" interface ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Interface";
        }

        if (line.StartsWith("struct ", StringComparison.Ordinal) || line.Contains(" struct ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Struct";
        }

        if (line.StartsWith("enum ", StringComparison.Ordinal) || line.Contains(" enum ", StringComparison.Ordinal))
        {
            name = ExtractNameAfterKeyword(line);
            return "Enum";
        }

        if (LooksLikeFunction(line))
        {
            name = ExtractFunctionName(line);
            return "Function";
        }

        if (LooksLikeConstant(line))
        {
            name = ExtractConstantName(line);
            return "Constant";
        }

        if (line.EndsWith("{", StringComparison.Ordinal))
        {
            return "Block";
        }

        return "Statement";
    }

    private static bool IsLikelyBlockContent(string line)
    {
        return !LooksLikeFunction(line) &&
               !LooksLikeConstant(line) &&
               !line.StartsWith("class ", StringComparison.Ordinal) &&
               !line.StartsWith("struct ", StringComparison.Ordinal) &&
               !line.StartsWith("enum ", StringComparison.Ordinal) &&
               !line.StartsWith("interface ", StringComparison.Ordinal) &&
               !line.StartsWith("namespace ", StringComparison.Ordinal);
    }

    private static bool LooksLikeFunction(string line)
    {
        return line.Contains('(') &&
               line.Contains(')') &&
               (line.EndsWith("{", StringComparison.Ordinal) ||
                line.EndsWith(":", StringComparison.Ordinal) ||
                line.EndsWith("=>", StringComparison.Ordinal) ||
                line.EndsWith(");", StringComparison.Ordinal) ||
                line.EndsWith(") {", StringComparison.Ordinal));
    }

    private static bool LooksLikeConstant(string line)
    {
        return line.Contains('=') &&
               !line.StartsWith("if ", StringComparison.Ordinal) &&
               !line.StartsWith("while ", StringComparison.Ordinal) &&
               !line.StartsWith("for ", StringComparison.Ordinal) &&
               !line.StartsWith("switch ", StringComparison.Ordinal);
    }

    private static string? ExtractNameAfterKeyword(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[1].Trim('{', '(', ':', ';') : null;
    }

    private static string? ExtractFunctionName(string line)
    {
        var openParen = line.IndexOf('(');
        if (openParen <= 0)
        {
            return null;
        }

        var prefix = line[..openParen].Trim();
        var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 ? tokens[^1].Trim('*', '&', ':') : null;
    }

    private static string? ExtractConstantName(string line)
    {
        var left = line.Split('=', 2)[0].Trim();
        var tokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 ? tokens[^1].Trim('*', '&', ':', ';', ',') : null;
    }
}
