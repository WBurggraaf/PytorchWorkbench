using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace SpecBuilder.Flows;

internal sealed class OllamaLanguagesFlow : IPipelineFlow
{
    private const int MaxSampleFiles = 4;
    private const int MaxPromptChars = 1800;
    private const int MaxPathChars = 120;
    private const int MaxContentChars = 700;

    private readonly string _indexPath;
    private readonly string[] _pathIgnorePaths;
    private readonly string _originRoot;
    private readonly string _outputRoot;
    private readonly string _indexOutputPath;
    private readonly string _ollamaEndpoint;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public OllamaLanguagesFlow(string workspaceRoot)
    {
        _indexPath = Path.Combine(workspaceRoot, "generated", "originfileindex.md");
        _originRoot = Path.Combine(workspaceRoot, "origin");
        _pathIgnorePaths =
        [
            Path.Combine(workspaceRoot, "pathignore.md"),
            Path.Combine(workspaceRoot, "SpecBuilder", "pathignore.md"),
            Path.Combine(workspaceRoot, "origin", "pathignore.md"),
        ];
        _outputRoot = Path.Combine(workspaceRoot, "generated", "languages");
        _indexOutputPath = Path.Combine(_outputRoot, "index.md");
        _ollamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";
        _model = Environment.GetEnvironmentVariable("SPECBUILDER_OLLAMA_MODEL") ?? "qwen3.5:0.8b";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_ollamaEndpoint),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public string Name => "2. Classify tech stacks";

    public string Description =>
        "Reads generated/originfileindex.md, asks local Ollama once per extension group, and writes per-category markdown files.";

    public async Task<FlowResult> ExecuteAsync()
    {
        if (!File.Exists(_indexPath))
        {
            return new FlowResult(
                $"Missing origin file index: {_indexPath}. Run step 1 first.",
                _indexOutputPath);
        }

        var generatedRoot = Path.GetDirectoryName(_indexPath) ?? _indexPath;
        var step1HashFile = Path.Combine(generatedRoot, ".step1-hash");
        var step2HashFile = Path.Combine(generatedRoot, ".step2-hash");

        if (File.Exists(step2HashFile) && File.Exists(step1HashFile) && File.Exists(_indexOutputPath))
        {
            var step1Hash = File.ReadAllText(step1HashFile).Trim();
            var step2Hash = File.ReadAllText(step2HashFile).Trim();
            if (step1Hash == step2Hash)
            {
                Console.WriteLine("[step2] ✓ source files unchanged - using cached tech stack classification");
                return new FlowResult("Loaded cached tech stack classification", _indexOutputPath);
            }
        }

        Console.WriteLine("[step2] source files changed or cache missing - running Ollama classification");
        var indexMarkdown = await File.ReadAllTextAsync(_indexPath, Encoding.UTF8);
        var ignorePatterns = LoadIgnorePatterns();
        var groups = ParseIndex(indexMarkdown)
            .Select(group => FilterGroup(group, ignorePatterns))
            .Where(group => group.Count > 0)
            .OrderBy(group => group.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            await WarmupAsync();
            var categories = await ClassifyPerGroupAsync(groups);
            await WriteCategoryFilesAsync(categories);
            await File.WriteAllTextAsync(_indexOutputPath, BuildIndexMarkdown(categories.Values), Encoding.UTF8);

            File.WriteAllText(step2HashFile, File.ReadAllText(step1HashFile));
            Console.WriteLine("[step2] saved cache marker");

            return new FlowResult(
                $"Wrote {categories.Count} tech/language category files from {groups.Count} extension groups.",
                _indexOutputPath);
        }
        catch (Exception ex)
        {
            return new FlowResult($"Ollama request failed: {ex.Message}", _indexOutputPath);
        }
    }

    private IReadOnlyList<string> LoadIgnorePatterns()
    {
        var ignorePath = _pathIgnorePaths.FirstOrDefault(File.Exists);
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

    private static ExtensionGroup FilterGroup(ExtensionGroup group, IReadOnlyList<string> ignorePatterns)
    {
        if (ignorePatterns.Count == 0)
        {
            return group;
        }

        var filteredFiles = group.Files
            .Where(file => !ShouldIgnore(file, ignorePatterns))
            .ToList();

        return group with
        {
            Files = filteredFiles,
            Count = filteredFiles.Count
        };
    }

    private static bool ShouldIgnore(string path, IReadOnlyList<string> ignorePatterns)
    {
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

            if (current is null)
            {
                continue;
            }

            if (trimmed.StartsWith("- Markdown:", StringComparison.OrdinalIgnoreCase))
            {
                current.MarkdownPath = trimmed["- Markdown:".Length..].Trim().Trim('`');
                continue;
            }

            if (trimmed.StartsWith("- `", StringComparison.Ordinal))
            {
                var file = trimmed.TrimStart('-', ' ').Trim('`').Trim();
                if (!string.IsNullOrWhiteSpace(file))
                {
                    current.Files.Add(file);
                }
            }
        }

        if (current is not null)
        {
            groups.Add(current.Build());
        }

        return groups;
    }

    private async Task<Dictionary<string, CategoryBucket>> ClassifyPerGroupAsync(IReadOnlyList<ExtensionGroup> groups)
    {
        var result = new Dictionary<string, CategoryBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var sample = BuildSample(group);
            var prompt = BuildPrompt(group, sample);
            Console.WriteLine("OLLAMA REQUEST");
            Console.WriteLine("--------------");
            Console.WriteLine($"Model: {_model}");
            Console.WriteLine($"Endpoint: {_ollamaEndpoint}");
            Console.WriteLine($"Extension: {group.Extension}");
            Console.WriteLine($"Sample files: {sample.Count}");
            Console.WriteLine($"Subset cap: {MaxSampleFiles} files / {MaxPromptChars} chars");
            Console.WriteLine($"Request estimate: ~{prompt.Length} chars");
            Console.WriteLine();
            Console.WriteLine(prompt);
            Console.WriteLine();

            var response = await AskOllamaStreamingAsync(prompt);
            Console.WriteLine("OLLAMA RESPONSE");
            Console.WriteLine("---------------");
            Console.WriteLine(response);
            Console.WriteLine();

            var classification = ParseClassification(response, group);
            var category = NormalizeName(classification.Category);
            if (!result.TryGetValue(category, out var bucket))
            {
                bucket = new CategoryBucket(category);
                result[category] = bucket;
            }

            bucket.Groups.Add(group);
            if (!string.IsNullOrWhiteSpace(classification.Note))
            {
                bucket.Notes.Add(classification.Note.Trim());
            }
        }

        return result;
    }

    private async Task WriteCategoryFilesAsync(Dictionary<string, CategoryBucket> categories)
    {
        Directory.CreateDirectory(_outputRoot);

        foreach (var bucket in categories.Values.OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase))
        {
            var outputPath = Path.Combine(_outputRoot, $"{SanitizeFileName(bucket.Category)}.md");
            var markdown = BuildCategoryMarkdown(bucket.Category, bucket.Notes, bucket.Groups);
            await File.WriteAllTextAsync(outputPath, markdown, Encoding.UTF8);
        }
    }

    private IReadOnlyList<string> BuildSample(ExtensionGroup group)
    {
        var files = group.SampleFiles;
        if (files.Count == 0)
        {
            return [group.RelativeMarkdownPath, $"files: {group.Count}"];
        }

        var selected = SelectRepresentativeFiles(files);
        if (selected.Count == 0)
        {
            selected = [group.RelativeMarkdownPath];
        }

        var clipped = new List<string>();
        var budget = 0;
        foreach (var file in selected)
        {
            var normalized = NormalizeSampleFile(file);
            var content = ReadSampleContent(file);
            var hints = BuildExtensionHints(group.Extension, file, content);
            var combined = BuildSampleBlock(normalized, content, hints);
            if (clipped.Count >= MaxSampleFiles)
            {
                break;
            }

            if (budget + combined.Length > MaxPromptChars)
            {
                break;
            }

            clipped.Add(combined);
            budget += combined.Length;
        }

        if (clipped.Count == 0)
        {
            clipped.Add(NormalizeSampleFile(selected[0]));
        }

        return clipped;
    }

    private static string BuildSampleBlock(string normalizedPath, string? content, IReadOnlyList<string> hints)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Path: {normalizedPath}");

        if (hints.Count > 0)
        {
            sb.AppendLine("Signals:");
            foreach (var hint in hints)
            {
                sb.AppendLine($"- {hint}");
            }
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine(content.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> BuildExtensionHints(string extension, string relativePath, string? content)
    {
        var hints = new List<string>();
        var normalizedPath = relativePath.Replace('\\', '/');
        var lowerPath = normalizedPath.ToLowerInvariant();
        var lowerContent = content?.ToLowerInvariant() ?? string.Empty;

        void AddIf(bool condition, string hint)
        {
            if (condition && !hints.Contains(hint, StringComparer.OrdinalIgnoreCase))
            {
                hints.Add(hint);
            }
        }

        switch (extension.ToLowerInvariant())
        {
            case ".cs":
            case ".csproj":
                AddIf(lowerContent.Contains("using ") || lowerContent.Contains("namespace ") || lowerContent.Contains("class "), "C# source structure with using/namespace/class declarations.");
                AddIf(lowerPath.Contains("test"), "May be a test or test helper file.");
                AddIf(lowerContent.Contains("async ") || lowerContent.Contains("await "), "Uses async/await style application logic.");
                break;
            case ".cpp":
            case ".cc":
            case ".cxx":
            case ".c":
            case ".h":
            case ".hpp":
            case ".hh":
            case ".inl":
                AddIf(lowerContent.Contains("#include"), "C or C++ source with include-heavy structure.");
                AddIf(lowerContent.Contains("namespace ") || lowerContent.Contains("class "), "Likely C++ code with namespaces or classes.");
                AddIf(lowerPath.Contains("test"), "May be test code or a test helper.");
                AddIf(lowerPath.Contains("cuda") || lowerContent.Contains("__global__") || lowerContent.Contains("__device__"), "May be GPU or low-level performance-oriented code.");
                break;
            case ".py":
            case ".pyi":
                AddIf(lowerContent.Contains("import ") || lowerContent.Contains("from "), "Python module with imports and top-level orchestration.");
                AddIf(lowerContent.Contains("def ") || lowerContent.Contains("class "), "Python logic organized around functions or classes.");
                AddIf(lowerPath.Contains("test"), "May be Python test or test support code.");
                break;
            case ".ps1":
                AddIf(lowerContent.Contains("param(") || lowerContent.Contains("function "), "PowerShell automation or wrapper script.");
                AddIf(lowerPath.Contains("test"), "May be a PowerShell test helper.");
                break;
            case ".bat":
            case ".cmd":
                AddIf(lowerContent.Contains("@echo off") || lowerContent.Contains("set "), "Windows command script for environment setup or orchestration.");
                AddIf(lowerPath.Contains("build"), "Likely build or setup automation.");
                AddIf(lowerPath.Contains("test"), "Likely test or validation automation.");
                break;
            case ".sh":
                AddIf(lowerContent.Contains("set -e") || lowerContent.Contains("#!/"), "Shell script for automation or build setup.");
                AddIf(lowerPath.Contains("test"), "May be a shell test helper.");
                break;
            case ".cmake":
                AddIf(lowerContent.Contains("add_executable") || lowerContent.Contains("add_library"), "CMake build definition for targets and dependencies.");
                AddIf(lowerContent.Contains("target_link_libraries") || lowerContent.Contains("find_package"), "Build configuration with dependency wiring.");
                break;
            case ".json":
                AddIf(lowerPath.Contains("package"), "Likely configuration or metadata.");
                AddIf(lowerContent.Contains("\"scripts\""), "May be a package or tooling manifest.");
                break;
            case ".md":
                AddIf(lowerPath.Contains("docs"), "Documentation or developer guidance.");
                AddIf(lowerPath.Contains("test"), "Test-related documentation or notes.");
                break;
            case ".yaml":
            case ".yml":
                AddIf(lowerPath.Contains(".github") || lowerPath.Contains("ci"), "CI or workflow configuration.");
                AddIf(lowerContent.Contains("name:") || lowerContent.Contains("steps:"), "Structured YAML for workflow or deployment configuration.");
                break;
        }

        return hints;
    }

    private static string BuildPrompt(ExtensionGroup group, IReadOnlyList<string> sampleFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Classify one extension group using the real file paths and code snippets below.");
        sb.AppendLine("Return exactly:");
        sb.AppendLine("CATEGORY: <label>");
        sb.AppendLine("NOTE: <short reason>");
        sb.AppendLine("If unsure, CATEGORY: Config.");
        sb.AppendLine("Labels: CSharp, Cpp, Python, PowerShell, CMake, Markdown, Shell, Json, TypeScript, JavaScript, Config, Solution, Batch, Yaml.");
        sb.AppendLine();
        sb.AppendLine($"Extension: {group.Extension}");
        sb.AppendLine($"File count: {group.Count}");
        sb.AppendLine("Files shown are only a subset.");
        sb.AppendLine("Each sample includes a path, extension-specific signals, and a short content excerpt.");
        foreach (var file in sampleFiles)
        {
            sb.AppendLine($"- {file}");
        }

        return sb.ToString();
    }

    private async Task<string> AskOllamaAsync(string prompt)
    {
        var payload = new
        {
            model = _model,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0,
                top_p = 1,
                num_predict = 24,
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", payload);
        response.EnsureSuccessStatusCode();

        var generation = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        var content = generation?.Response?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama returned empty content.");
        }

        return content;
    }

    private async Task<string> AskOllamaStreamingAsync(string prompt)
    {
        var payload = new
        {
            model = _model,
            prompt,
            stream = true,
            think = false,
            options = new
            {
                temperature = 0,
                top_p = 1,
                num_predict = 24,
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(payload)
        };

        var sw = Stopwatch.StartNew();
        var sawThinking = false;
        var sawResponse = false;
        var firstTokenLogged = false;
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), heartbeatCts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (!firstTokenLogged)
                {
                    Console.Write(".");
                }
            }
        }, heartbeatCts.Token);
        Console.WriteLine("OLLAMA STREAM");
        Console.WriteLine("-------------");
        Console.WriteLine($"Prompt chars: {prompt.Length}");
        Console.WriteLine("waiting for first token...");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var output = new StringBuilder();
        var rawPreview = new List<string>();
        var sawDone = false;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (rawPreview.Count < 3)
            {
                rawPreview.Add(line);
            }

            using var document = JsonDocument.Parse(line);
            string? chunk = null;
            if (document.RootElement.TryGetProperty("response", out var responseElement))
            {
                chunk = responseElement.GetString();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    sawResponse = true;
                }
            }
            else if (document.RootElement.TryGetProperty("thinking", out var thinkingElement))
            {
                var thinking = thinkingElement.GetString();
                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    sawThinking = true;
                    if (!firstTokenLogged)
                    {
                        Console.WriteLine($"first token at {sw.ElapsedMilliseconds} ms");
                        firstTokenLogged = true;
                    }

                    Console.Write($"[thinking]{thinking}");
                }
            }
            else if (document.RootElement.TryGetProperty("message", out var messageElement) &&
                     messageElement.TryGetProperty("content", out var contentElement))
            {
                chunk = contentElement.GetString();
            }
            else if (document.RootElement.TryGetProperty("content", out var contentOnlyElement))
            {
                chunk = contentOnlyElement.GetString();
            }

            if (!string.IsNullOrEmpty(chunk))
            {
                if (!firstTokenLogged)
                {
                    Console.WriteLine($"first token at {sw.ElapsedMilliseconds} ms");
                    firstTokenLogged = true;
                }

                Console.Write(chunk);
                output.Append(chunk);
            }

            if (document.RootElement.TryGetProperty("done", out var doneElement) && doneElement.GetBoolean())
            {
                sawDone = true;
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"completed in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"stream flags: thinking={(sawThinking ? "yes" : "no")}, response={(sawResponse ? "yes" : "no")}");
        heartbeatCts.Cancel();
        try
        {
            await heartbeat;
        }
        catch (TaskCanceledException)
        {
        }

        var content = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            var preview = rawPreview.Count > 0 ? string.Join(" || ", rawPreview) : "(no stream lines)";
            throw new InvalidOperationException($"Ollama returned empty content. Done={sawDone}. Preview: {preview}");
        }

        return content;
    }

    private static CategoryClassification ParseClassification(string response, ExtensionGroup group)
    {
        var category = ReadLabel(response, "CATEGORY") ?? InferFallbackCategory(group.Extension);
        var note = ReadLabel(response, "NOTE");
        return new CategoryClassification(category, note);
    }

    private static string InferFallbackCategory(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" or ".csproj" => "CSharp",
            ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh" or ".inl" => "Cpp",
            ".py" or ".pyi" => "Python",
            ".ps1" => "PowerShell",
            ".cmd" or ".bat" => "Batch",
            ".sh" => "Shell",
            ".cmake" => "CMake",
            ".md" => "Markdown",
            ".json" => "Json",
            ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript",
            ".yaml" or ".yml" => "Yaml",
            ".sln" => "Solution",
            _ => "Config"
        };
    }

    private static string? ReadLabel(string content, string label)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(label.Length + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private async Task WarmupAsync()
    {
        var payload = new
        {
            model = _model,
            prompt = "CATEGORY: Config\nNOTE: warmup",
            stream = false,
            options = new
            {
                temperature = 0,
                top_p = 1,
                num_predict = 8,
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", payload);
        response.EnsureSuccessStatusCode();
    }

    private static string NormalizeName(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
        return cleaned.Replace('/', '-');
    }

    private static List<string> SelectRepresentativeFiles(IReadOnlyList<string> files)
    {
        var selected = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (selected.Count >= MaxSampleFiles)
            {
                break;
            }

            var directory = GetParentDirectory(file);
            var token = GetClassifyingToken(file);
            var shouldTake = selected.Count == 0 || seenDirectories.Add(directory) || seenTokens.Add(token);

            if (!shouldTake)
            {
                continue;
            }

            selected.Add(file);
        }

        if (selected.Count < MaxSampleFiles)
        {
            foreach (var file in files)
            {
                if (selected.Count >= MaxSampleFiles)
                {
                    break;
                }

                if (!selected.Any(existing => existing.Equals(file, StringComparison.OrdinalIgnoreCase)))
                {
                    selected.Add(file);
                }
            }
        }

        return selected;
    }

    private static string GetParentDirectory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : normalized[..lastSlash];
    }

    private static string GetClassifyingToken(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return normalized;
        }

        if (parts.Length == 1)
        {
            return parts[0];
        }

        var tail = parts[^1];
        var head = parts[0];
        var parent = parts.Length >= 2 ? parts[^2] : string.Empty;
        return $"{head}/{parent}/{tail}";
    }

    private static string NormalizeSampleFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Length <= MaxPathChars ? normalized : "..." + normalized[^Math.Min(MaxPathChars - 3, normalized.Length)..];
    }

    private string? ReadSampleContent(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_originRoot, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_originRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return null;
        }

        var content = File.ReadAllText(fullPath, Encoding.UTF8)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        if (content.Length <= MaxContentChars)
        {
            return $"Content:\n{content}";
        }

        var head = content[..Math.Min(MaxContentChars, content.Length)];
        return $"Content excerpt:\n{head}";
    }

    private static string SanitizeFileName(string value)
    {
        var chars = value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned.Trim('_');
    }

    private static string BuildIndexMarkdown(IEnumerable<CategoryBucket> categories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Language / Tech Stack Index");
        sb.AppendLine();
        sb.AppendLine($"- Source index: `generated/originfileindex.md`");
        sb.AppendLine($"- Generated: `{DateTimeOffset.Now:O}`");
        sb.AppendLine();
        sb.AppendLine("## Categories");
        sb.AppendLine();
        sb.AppendLine("| Category | Extensions | Files | Markdown |");
        sb.AppendLine("| --- | --- | ---: | --- |");

        foreach (var item in categories.OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase))
        {
            var extensions = item.Groups.Select(x => x.Extension).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x);
            sb.AppendLine($"| `{item.Category}` | `{string.Join(", ", extensions)}` | {item.Groups.Sum(x => x.Count)} | `{RelativeCategoryPath(item.Category)}` |");
        }

        return sb.ToString();
    }

    private static string RelativeCategoryPath(string category) => $"languages/{SanitizeFileName(category)}.md";

    private static string BuildCategoryMarkdown(
        string category,
        IReadOnlyCollection<string> notes,
        IReadOnlyCollection<ExtensionGroup> groups)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {category}");
        sb.AppendLine();
        sb.AppendLine($"- Extensions: `{string.Join(", ", groups.Select(x => x.Extension))}`");
        sb.AppendLine($"- File count: `{groups.Sum(x => x.Count)}`");
        sb.AppendLine();

        if (notes.Count > 0)
        {
            sb.AppendLine("## Ollama Notes");
            sb.AppendLine();
            foreach (var note in notes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {note}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Extension Groups");
        sb.AppendLine();
        foreach (var group in groups.OrderBy(x => x.Extension, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"- `{group.Extension}`: `{group.Count}` files -> `{group.RelativeMarkdownPath}`");
            foreach (var file in group.SampleFiles.Take(MaxSampleFiles))
            {
                sb.AppendLine($"  - sample: `{file}`");
            }
        }

        return sb.ToString();
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }

    private sealed record CategoryClassification(string Category, string? Note);

    private sealed class CategoryBucket
    {
        public CategoryBucket(string category)
        {
            Category = category;
        }

        public string Category { get; }
        public List<string> Notes { get; } = new();
        public List<ExtensionGroup> Groups { get; } = new();
    }

    private sealed record ExtensionGroup(string Extension, int Count, string RelativeMarkdownPath, IReadOnlyList<string> SampleFiles)
    {
        public IReadOnlyList<string> Files
        {
            get => SampleFiles;
            init => SampleFiles = value;
        }
    }

    private sealed class ExtensionGroupBuilder
    {
        public ExtensionGroupBuilder(string extension)
        {
            Extension = extension;
        }

        public string Extension { get; }
        public string MarkdownPath { get; set; } = string.Empty;
        public List<string> Files { get; } = new();

        public ExtensionGroup Build() => new(
            Extension,
            Files.Count,
            MarkdownPath,
            Files.ToList());
    }
}
