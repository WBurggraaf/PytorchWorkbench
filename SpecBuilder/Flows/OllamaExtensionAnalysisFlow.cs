using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpecBuilder.Flows;

internal sealed class OllamaExtensionAnalysisFlow : IPipelineFlow
{
    private const int MaxPromptChars = 18000;
    private const int MaxChunkContentChars = 2400;
    private const int MaxSingleCallChars = 6000;
    private const int MaxSafeSingleCallChars = 7000;
    private const int DefaultStep3PredictTokens = 2048;
    private const int MinStep3PredictTokens = 512;
    private const int MaxStep3PredictTokens = 8192;
    private const int MaxReportFileNameChars = 100;
    private const int MaxParallelFiles = 2;

    private readonly string _indexPath;
    private readonly string _outputRoot;
    private readonly string _reportIndexPath;
    private readonly string _cachePath;
    private readonly string _workspaceRoot;
    private readonly string _originRoot;
    private readonly string _ollamaEndpoint;
    private readonly string _model;
    private readonly string _skillPrompt;
    private readonly int _singleCallChars;
    private readonly int _step3PredictTokens;
    private readonly HttpClient _httpClient;

    public OllamaExtensionAnalysisFlow(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _originRoot = Path.Combine(workspaceRoot, "origin");
        _indexPath = Path.Combine(workspaceRoot, "generated", "originfileindex.md");
        _outputRoot = Path.Combine(workspaceRoot, "generated", "extension-analysis");
        _reportIndexPath = Path.Combine(_outputRoot, "index.md");
        _cachePath = Path.Combine(workspaceRoot, "generated", "extension-analysis-cache.json");
        _ollamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";
        _model = Environment.GetEnvironmentVariable("SPECBUILDER_OLLAMA_MODEL_STEP3") ??
                 Environment.GetEnvironmentVariable("SPECBUILDER_OLLAMA_MODEL") ??
                 "qwen3.5:0.8b";
        _skillPrompt = LoadSkillPrompt(workspaceRoot);
        _singleCallChars = GetConfiguredSingleCallChars();
        _step3PredictTokens = GetConfiguredStep3PredictTokens();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_ollamaEndpoint),
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    public string Name => "3. Explain extension purpose from filepaths";

    public string Description =>
        "Reads generated/originfileindex.md, lets you choose extension groups, analyzes each file path one by one, and writes a fresh plain-text dump per group.";

    public async Task<FlowResult> ExecuteAsync()
    {
        if (!File.Exists(_indexPath))
        {
            return new FlowResult($"Missing origin file index: {_indexPath}. Run step 1 first.", _indexPath);
        }

        var indexMarkdown = await File.ReadAllTextAsync(_indexPath, Encoding.UTF8);
        var groups = ParseIndex(indexMarkdown).OrderBy(group => group.Extension, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count == 0)
        {
            return new FlowResult("No extension groups found in originfileindex.md.", _indexPath);
        }

        var selectedGroups = SelectGroups(groups);
        if (selectedGroups.Count == 0)
        {
            return new FlowResult("No extension groups selected.", _reportIndexPath);
        }

        try
        {
            await WarmupAsync();

            Directory.CreateDirectory(_outputRoot);

            var reportEntries = new List<ReportEntry>();
            var cache = LoadCache();
            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");

            foreach (var group in selectedGroups)
            {
                var groupRunId = $"{runId}-{SanitizeFileName(group.Extension.TrimStart('.'))}";
                var reportName = BuildReportFileName(group.Extension, group.Files, groupRunId);
                var reportPath = Path.Combine(_outputRoot, reportName);

                Console.WriteLine("OLLAMA EXTENSION ANALYSIS");
                Console.WriteLine("--------------------------");
                Console.WriteLine($"Extension: {group.Extension}");
                Console.WriteLine($"Files: {group.Files.Count}");
                Console.WriteLine($"Model: {_model}");
                Console.WriteLine($"Single-call threshold: {_singleCallChars} chars");
                Console.WriteLine($"Parallel files: {MaxParallelFiles}");
                Console.WriteLine($"Chunk size: {GetChunkSizeForExtension(group.Extension)} chars");
                Console.WriteLine($"Report: {reportName}");
                Console.WriteLine();

                var dump = await BuildGroupDumpAsync(group, groupRunId, cache);
                await WriteAtomicAsync(reportPath, dump);
                reportEntries.Add(new ReportEntry(group.Extension, reportName, group.Files.Count));

                Console.WriteLine($"Wrote report: {reportPath}");
                Console.WriteLine();
            }

            await SaveCacheAsync(cache);
            await File.WriteAllTextAsync(_reportIndexPath, BuildReportIndex(reportEntries), Encoding.UTF8);
            return new FlowResult($"Wrote {reportEntries.Count} fresh extension analysis reports.", _reportIndexPath);
        }
        catch (Exception ex)
        {
            return new FlowResult($"Extension analysis failed: {ex.Message}", _outputRoot);
        }
    }

    private async Task<string> BuildGroupDumpAsync(ExtensionGroup group, string runId, AnalysisCacheStore cache)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ANALYSIS_DUMP_VERSION: 1");
        sb.AppendLine($"RUN_ID: {runId}");
        sb.AppendLine($"ROOT_PATH: {_workspaceRoot}");
        sb.AppendLine($"FILE_COUNT: {group.Files.Count}");
        sb.AppendLine();

        var semaphore = new SemaphoreSlim(MaxParallelFiles, MaxParallelFiles);
        var tasks = group.Files.Select((file, index) => AnalyzeFileEntryAsync(group.Extension, index + 1, group.Files.Count, file, runId, cache, semaphore)).ToList();
        var results = await Task.WhenAll(tasks);

        foreach (var result in results.OrderBy(item => item.Index))
        {
            sb.AppendLine(result.Record.TrimEnd());
            sb.AppendLine();
        }

        sb.AppendLine("END_ANALYSIS_DUMP");
        return sb.ToString().TrimEnd();
    }

    private async Task<FileRecordResult> AnalyzeFileEntryAsync(string extension, int fileNumber, int fileCount, string file, string runId, AnalysisCacheStore cache, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var record = await AnalyzeFileWithChunksAsync(extension, fileNumber, fileCount, file, runId, cache);
            return new FileRecordResult(fileNumber, record);
        }
        finally
        {
            semaphore.Release();
        }
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

    private static IReadOnlyList<ExtensionGroup> SelectGroups(IReadOnlyList<ExtensionGroup> groups)
    {
        while (true)
        {
            Console.WriteLine("Select extension groups to analyze:");
            Console.WriteLine("A. All groups");
            for (var i = 0; i < groups.Count; i++)
            {
                Console.WriteLine($"{i + 1}. `{groups[i].Extension}` ({groups[i].Files.Count} files)");
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

    private static IReadOnlyList<ExtensionGroup> ParseSelection(string input, IReadOnlyList<ExtensionGroup> groups)
    {
        var indices = new SortedSet<int>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var dash = part.IndexOf('-');
                if (dash <= 0 || dash >= part.Length - 1)
                {
                    continue;
                }

                if (!int.TryParse(part[..dash], out var start) || !int.TryParse(part[(dash + 1)..], out var end))
                {
                    continue;
                }

                if (start > end)
                {
                    (start, end) = (end, start);
                }

                for (var i = start; i <= end; i++)
                {
                    if (i >= 1 && i <= groups.Count)
                    {
                        indices.Add(i - 1);
                    }
                }

                continue;
            }

            if (int.TryParse(part, out var single) && single >= 1 && single <= groups.Count)
            {
                indices.Add(single - 1);
            }
        }

        return indices.Select(index => groups[index]).ToList();
    }

    private async Task<string> AnalyzeFileWithChunksAsync(string extension, int fileNumber, int fileCount, string file, string runId, AnalysisCacheStore cache)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_originRoot, file));
        var fileHash = ComputeFileHash(fullPath);
        var cacheKey = BuildCacheKey(extension, file, fileHash);
        if (cache.Files.TryGetValue(cacheKey, out var cached) && string.Equals(cached.ContentHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"CACHE HIT [{extension}] FILE {fileNumber}/{fileCount}");
            return cached.Record;
        }

        var chunks = ReadOriginFileChunks(file, GetChunkSizeForExtension(extension)).ToList();
        if (chunks.Count == 0)
        {
            chunks = [new FileChunk(1, 1, "unknown", 1, 1)];
        }

        Console.WriteLine($"CACHE MISS [{extension}] FILE {fileNumber}/{fileCount}");
        Console.WriteLine($"- path: {file}");
            Console.WriteLine($"- chunks: {chunks.Count}");
            Console.WriteLine($"- chars: {GetFileCharCount(fullPath)}");
            Console.WriteLine($"- estimated prompt chars: {EstimatePromptChars(extension, file, runId, chunks)}");
            Console.WriteLine($"- effective chunk size: {GetChunkSizeForExtension(extension)}");

        if (chunks.Count == 1 || EstimatePromptChars(extension, file, runId, chunks) <= _singleCallChars)
        {
            Console.WriteLine($"FAST PATH [{extension}] FILE {fileNumber}/{fileCount}: single Ollama call");
            var direct = await AskOllamaForFileAsync(extension, fileNumber, fileCount, file, runId, chunks[0], cache);
            var fileRecord = direct;
            cache.Files[cacheKey] = new CachedFileAnalysis(fileHash, fileRecord);
            await SaveCacheAsync(cache);
            return fileRecord;
        }

        var analyses = new List<FileAnalysis>();
        foreach (var chunk in chunks)
        {
            var analysis = await AskOllamaForChunkAsync(extension, fileNumber, fileCount, file, runId, chunk, cache);
            analyses.Add(analysis);
        }

        var record = await BuildFinalExplanationAsync(extension, fileNumber, fileCount, file, runId, analyses);
        cache.Files[cacheKey] = new CachedFileAnalysis(fileHash, record);
        await SaveCacheAsync(cache);
        return record;
    }

    private static long GetFileCharCount(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetConfiguredSingleCallChars()
    {
        var raw = Environment.GetEnvironmentVariable("SPECBUILDER_STEP3_SINGLE_CALL_CHARS");
        if (!int.TryParse(raw, out var configured))
        {
            return 6000;
        }

        return Math.Clamp(configured, 3000, MaxSafeSingleCallChars);
    }

    private static int GetConfiguredStep3PredictTokens()
    {
        var raw = Environment.GetEnvironmentVariable("SPECBUILDER_STEP3_NUM_PREDICT");
        if (!int.TryParse(raw, out var configured))
        {
            return DefaultStep3PredictTokens;
        }

        return Math.Clamp(configured, MinStep3PredictTokens, MaxStep3PredictTokens);
    }

    private static int EstimatePromptChars(string extension, string filePath, string runId, IReadOnlyList<FileChunk> chunks)
    {
        var templateOverhead = 1400;
        var fileMetadata = extension.Length + filePath.Length + runId.Length + 200;
        var chunkContent = chunks.Sum(chunk => chunk.Content.Length);
        return templateOverhead + fileMetadata + chunkContent;
    }

    private static string LoadSkillPrompt(string workspaceRoot)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SPECBUILDER_STEP3_SKILL_PATH"),
            Path.Combine(workspaceRoot, "step3-skill.md"),
            Path.Combine(workspaceRoot, "SpecBuilder", "step3-skill.md"),
            Path.Combine(workspaceRoot, "generated", "step3-skill.md"),
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var text = File.ReadAllText(candidate, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return """
        ROLE:
        You are an expert senior software engineer and performance reviewer.

        PRIORITIES:
        1. Explain the code clearly and completely.
        2. Keep the core explainer accurate and human-readable.
        3. Add short extra remarks about performance inefficiencies, CPU waste, bad practices, or avoidable complexity when the code suggests them.
        4. Keep those extra remarks secondary; do not let them replace the main explanation.

        STYLE:
        - Stay grounded in the actual language and domain of the file.
        - Prefer specific observations over generic advice.
        - Mention likely performance, waiting, or efficiency concerns only when the source supports them.
        - Do not overfocus on performance if the file is mostly glue, config, or simple orchestration.
        """.Trim();
    }

    private string ComposeSkillPrompt(string body)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_skillPrompt))
        {
            sb.AppendLine("EXPERT_SKILL<<BLOCK");
            sb.AppendLine(_skillPrompt.Trim());
            sb.AppendLine("BLOCK");
            sb.AppendLine();
        }

        sb.Append(body.Trim());
        return sb.ToString();
    }

    private string BuildPrompt(string rootPath, string extension, int fileNumber, int fileCount, string filePath, string fileContent, string runId, int chunkIndex, int chunkCount, int startLine, int endLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Explain what this code chunk does in a human-friendly way.");
        sb.AppendLine("Write a detailed explanation with real nuance, not a short summary.");
        sb.AppendLine("Also add a separate critical review of likely flaws, inefficiencies, and bad practices you can spot.");
        sb.AppendLine("Keep the review secondary to the explanation, but make it concrete.");
        sb.AppendLine("Use as much space as needed, but keep it readable.");
        sb.AppendLine("Do not be technical unless the code domain requires it.");
        sb.AppendLine("Do not use JSON. Do not use Markdown. Do not use bullets.");
        sb.AppendLine("Return exactly these labeled fields:");
        sb.AppendLine("FILE_PATH: <full path>");
        sb.AppendLine($"FILE_LINE_RANGE: {startLine}-{endLine}");
        sb.AppendLine("FULL_DESCRIPTION: <one detailed paragraph>");
        sb.AppendLine("AUDIT_NOTES: <one detailed paragraph>");
        sb.AppendLine("AUDIT_LOCATIONS: <machine-readable issue list with ISSUE_LINE_RANGE: start-end and short note>");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Focus only on what this chunk is trying to do.");
        sb.AppendLine("- Explain the role of the code in plain language.");
        sb.AppendLine("- Mention the code domain when it matters, but keep jargon minimal.");
        sb.AppendLine("- If the chunk is not enough to know everything, explain the likely purpose from the available code.");
        sb.AppendLine("- Also call out suspicious patterns, avoidable work, or performance concerns when the source supports them.");
        sb.AppendLine("- When you mention a flaw or concern, include the exact line range using ISSUE_LINE_RANGE: start-end.");
        sb.AppendLine();
        sb.AppendLine($"Extension: {extension}");
        sb.AppendLine($"File: {fileNumber}/{fileCount}");
        sb.AppendLine($"Path: {filePath}");
        sb.AppendLine($"Chunk: {chunkIndex}/{chunkCount}");
        sb.AppendLine($"ChunkLineRange: {startLine}-{endLine}");
        sb.AppendLine();
        sb.AppendLine("FILE_CONTENT<<BLOCK");
        sb.AppendLine(fileContent.TrimEnd());
        sb.AppendLine("BLOCK");

        var prompt = ComposeSkillPrompt(sb.ToString());
        return TrimPromptByContent(prompt, fileContent, "FILE_CONTENT<<BLOCK", "BLOCK");
    }

    private string BuildFilePrompt(string rootPath, string extension, int fileNumber, int fileCount, string filePath, string fileContent, string runId, int startLine, int endLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Explain this whole code file in a machine-readable plain-text format.");
        sb.AppendLine("Keep the explanation detailed and nuanced.");
        sb.AppendLine("Also include a separate critical review section with likely flaws, inefficiencies, and bad practices.");
        sb.AppendLine("Do not use JSON. Do not use Markdown. Do not use bullets.");
        sb.AppendLine("Return exactly these labeled fields:");
        sb.AppendLine("FILE_PATH: <full path>");
        sb.AppendLine("SUMMARY: <one detailed paragraph>");
        sb.AppendLine("PURPOSE: <one detailed paragraph>");
        sb.AppendLine("HOW_IT_WORKS: <one detailed paragraph>");
        sb.AppendLine("WHY_IT_EXISTS: <one detailed paragraph>");
        sb.AppendLine("INPUTS: <comma-separated list or unknown>");
        sb.AppendLine("OUTPUTS: <comma-separated list or unknown>");
        sb.AppendLine("DEPENDENCIES: <comma-separated list or unknown>");
        sb.AppendLine("BEHAVIOR_NOTES: <one detailed paragraph>");
        sb.AppendLine("AUDIT_NOTES: <one detailed paragraph with critique, inefficiencies, and review observations>");
        sb.AppendLine("AUDIT_LOCATIONS: <machine-readable issue list with ISSUE_LINE_RANGE: start-end and short note>");
        sb.AppendLine("UNCERTAINTY: <one detailed paragraph>");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Keep the detail level high.");
        sb.AppendLine("- Preserve nuance and real code-specific behavior.");
        sb.AppendLine("- Stay understandable to a non-expert.");
        sb.AppendLine("- If uncertain, say what is likely rather than collapsing to a generic summary.");
        sb.AppendLine("- Make the audit notes useful for later review, even if the code is broadly correct.");
        sb.AppendLine("- In AUDIT_LOCATIONS, always include line ranges for each concern when possible.");
        sb.AppendLine();
        sb.AppendLine($"Extension: {extension}");
        sb.AppendLine($"File: {fileNumber}/{fileCount}");
        sb.AppendLine($"Path: {filePath}");
        sb.AppendLine($"RUN_ID: {runId}");
        sb.AppendLine($"FILE_LINE_RANGE: {startLine}-{endLine}");
        sb.AppendLine();
        sb.AppendLine("FILE_CONTENT<<BLOCK");
        sb.AppendLine(fileContent.TrimEnd());
        sb.AppendLine("BLOCK");

        var prompt = ComposeSkillPrompt(sb.ToString());
        return TrimPromptByContent(prompt, fileContent, "FILE_CONTENT<<BLOCK", "BLOCK");
    }

    private static bool TryNormalizeSingleFileDump(string response, string runId, string rootPath, int fileCount, int fileIndex, string filePath, string extension, out FileAnalysis analysis)
    {
        var normalized = response.Trim();
        if (IsPlaceholderResponse(normalized))
        {
            analysis = default!;
            return false;
        }

        analysis = new FileAnalysis(
            filePath,
            InferLanguageFromExtension(extension),
            InferArchComponentFromExtension(extension),
            "medium",
            normalized,
            "unknown",
            ReadList(normalized, "INPUTS"),
            ReadList(normalized, "OUTPUTS"),
            ReadList(normalized, "SIDE_EFFECTS"),
            ReadList(normalized, "DEPENDENCIES"),
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            ["unknown"],
            ["unknown"],
            ["unknown"],
            ["unknown"],
            "unknown",
            ["unknown"],
            ["unknown"],
            ["unknown"],
            ReadLabel(normalized, "AUDIT_NOTES") ?? "unknown",
            ReadList(normalized, "AUDIT_LOCATIONS"));
        return true;
    }

    private async Task<string> AskOllamaForFileAsync(string extension, int fileNumber, int fileCount, string file, string runId, FileChunk chunk, AnalysisCacheStore cache)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_originRoot, file));
        var fileHash = ComputeFileHash(fullPath);
        var chunkHash = ComputeChunkHash(chunk.Content);
        var chunkKey = BuildChunkCacheKey(extension, file, fileHash, 0, chunkHash);
        if (cache.Chunks.TryGetValue(chunkKey, out var cachedChunk) && string.Equals(cachedChunk.ContentHash, chunkHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"FILE CACHE HIT [{extension}] FILE {fileNumber}/{fileCount}");
            return cachedChunk.Analysis.FunctionalSummary;
        }

        var prompt = BuildFilePrompt(_workspaceRoot, extension, fileNumber, fileCount, file, chunk.Content, runId, chunk.StartLine, chunk.EndLine);
        Console.WriteLine($"OLLAMA FILE [{extension}] FILE {fileNumber}/{fileCount}");
        Console.WriteLine("----------------------------------------------");
        Console.WriteLine($"Request estimate: ~{prompt.Length} chars");
        Console.WriteLine($"File line range: {chunk.StartLine}-{chunk.EndLine}");
        Console.WriteLine(prompt);
        Console.WriteLine();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await AskOllamaStreamingAsync(prompt);
            if (TryNormalizeFinalExplanation(response, file, out var normalized))
            {
                var analysis = new FileAnalysis(
                    file,
                    InferLanguageFromExtension(extension),
                    InferArchComponentFromExtension(extension),
                    "medium",
                    normalized,
                    "unknown",
                    ["unknown"],
                    ["unknown"],
                    ["unknown"],
                    ["unknown"],
                    "unknown",
                    "unknown",
                    "unknown",
                    "unknown",
                    "unknown",
                    "unknown",
                    ["unknown"],
                    ["unknown"],
                    ["unknown"],
                    ["unknown"],
                    "unknown",
                    ["unknown"],
                    ["unknown"],
                    ["unknown"],
                    "unknown",
                    ["unknown"]);

                cache.Chunks[chunkKey] = new CachedChunkAnalysis(chunkHash, analysis);
                await SaveCacheAsync(cache);
                return normalized;
            }

            Console.WriteLine($"Placeholder or empty file synthesis for file {fileNumber}/{fileCount}. attempt {attempt}");
            Console.WriteLine();
        }

        return BuildFallbackExplanation(file, chunk.Content);
    }

    private async Task<string> BuildFinalExplanationAsync(string extension, int fileNumber, int fileCount, string filePath, string runId, IReadOnlyList<FileAnalysis> analyses)
    {
        var chunkSummaries = string.Join("\n\n", analyses.Select((analysis, index) =>
            $"Chunk {index + 1} explanation:\n{analysis.FunctionalSummary}\nChunk {index + 1} audit:\n{analysis.TechnicalSummary}"));
        var prompt = new StringBuilder()
            .AppendLine("Rewrite these chunk explanations into one continuous combined description of the whole file.")
            .AppendLine("Do not throw away nuance. Preserve the important details from the chunks.")
            .AppendLine("Do not compress everything into a generic summary.")
            .AppendLine("Make the result read naturally as one coherent explanation.")
            .AppendLine("Also rewrite the audit observations into one separate critical-review section.")
            .AppendLine("The audit section should call out flaws, inefficiencies, risk points, and review remarks without being the main story.")
            .AppendLine("Do not use JSON. Do not use Markdown. Do not use bullets.")
            .AppendLine("Return exactly these labeled fields:")
            .AppendLine("FILE_PATH: <full path>")
            .AppendLine("FULL_DESCRIPTION: <one long, combined, natural-language description>")
            .AppendLine("AUDIT_NOTES: <one long, combined, natural-language review of issues and inefficiencies>")
            .AppendLine("AUDIT_LOCATIONS: <machine-readable issue list with ISSUE_LINE_RANGE: start-end and short note>")
            .AppendLine()
            .AppendLine($"Extension: {extension}")
            .AppendLine($"File: {fileNumber}/{fileCount}")
            .AppendLine($"Path: {filePath}")
            .AppendLine()
            .AppendLine("Chunk notes:")
            .AppendLine(chunkSummaries)
            .ToString();
        prompt = ComposeSkillPrompt(prompt);

        Console.WriteLine($"OLLAMA SYNTHESIS [{extension}] FILE {fileNumber}/{fileCount}");
        Console.WriteLine("----------------------------------------------");
        Console.WriteLine($"Request estimate: ~{prompt.Length} chars");
        Console.WriteLine(prompt);
        Console.WriteLine();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await AskOllamaStreamingAsync(prompt);
            if (TryNormalizeFinalExplanation(response, filePath, out var normalized))
            {
                return normalized;
            }

            Console.WriteLine($"Placeholder or empty synthesis for file {fileNumber}/{fileCount}. attempt {attempt}");
            Console.WriteLine();
        }

        var fallback = analyses.Select(a => a.FunctionalSummary).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "unknown";
        return BuildFallbackExplanation(filePath, fallback);
    }

    private static string TrimPromptByContent(string prompt, string content, string startMarker, string endMarker)
    {
        if (prompt.Length <= MaxPromptChars)
        {
            return prompt;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return prompt[..MaxPromptChars];
        }

        var markerStart = prompt.IndexOf(startMarker, StringComparison.Ordinal);
        var markerEnd = prompt.IndexOf(endMarker, markerStart >= 0 ? markerStart + startMarker.Length : 0, StringComparison.Ordinal);
        if (markerStart < 0 || markerEnd < 0 || markerEnd <= markerStart)
        {
            return prompt[..MaxPromptChars];
        }

        var prefix = prompt[..(markerStart + startMarker.Length)];
        var suffix = prompt[markerEnd..];
        var budget = MaxPromptChars - prefix.Length - suffix.Length - Environment.NewLine.Length * 2;
        if (budget <= 0)
        {
            return prompt[..MaxPromptChars];
        }

        var trimmedContent = content.Length <= budget ? content : content[..budget];
        var rebuilt = new StringBuilder(prefix.Length + trimmedContent.Length + suffix.Length + 16)
            .Append(prefix)
            .AppendLine()
            .AppendLine(trimmedContent)
            .Append(suffix)
            .ToString();

        return rebuilt.Length <= MaxPromptChars ? rebuilt : rebuilt[..MaxPromptChars];
    }

    private static bool TryNormalizeFinalExplanation(string response, string filePath, out string normalized)
    {
        var text = response.Trim();
        var requiredLabels = new[]
        {
            "FILE_PATH:",
            "FULL_DESCRIPTION:",
            "AUDIT_NOTES:",
            "AUDIT_LOCATIONS:"
        };

        var hasAllLabels = requiredLabels.All(label => text.Contains(label, StringComparison.OrdinalIgnoreCase));
        if (hasAllLabels && !IsPlaceholderResponse(text))
        {
            normalized = NormalizeFinalText(text, filePath);
            return true;
        }

        normalized = BuildFallbackExplanation(filePath, text);
        return false;
    }

    private static string NormalizeFinalText(string text, string filePath)
    {
        var lines = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var description = ReadLabel(lines, "FULL_DESCRIPTION") ?? text;
        var audit = ReadLabel(lines, "AUDIT_NOTES") ?? "unknown";
        var auditLocations = ReadLabel(lines, "AUDIT_LOCATIONS") ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine($"FILE_PATH: {filePath}");
        sb.AppendLine($"FULL_DESCRIPTION: {description.Trim()}");
        sb.AppendLine($"AUDIT_NOTES: {audit.Trim()}");
        sb.AppendLine($"AUDIT_LOCATIONS: {auditLocations.Trim()}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFallbackExplanation(string filePath, string explanation)
    {
        var cleaned = string.IsNullOrWhiteSpace(explanation) ? "unknown" : explanation.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"FILE_PATH: {filePath}");
        sb.AppendLine($"FULL_DESCRIPTION: {cleaned}");
        sb.AppendLine("AUDIT_NOTES: unknown");
        sb.AppendLine("AUDIT_LOCATIONS: unknown");
        return sb.ToString().TrimEnd();
    }

    private static string? ReadLabel(IEnumerable<string> lines, string label)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(label.Length + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private async Task<FileAnalysis> AskOllamaForChunkAsync(string extension, int fileNumber, int fileCount, string file, string runId, FileChunk chunk, AnalysisCacheStore cache)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_originRoot, file));
        var fileHash = ComputeFileHash(fullPath);
        var chunkHash = ComputeChunkHash(chunk.Content);
        var chunkKey = BuildChunkCacheKey(extension, file, fileHash, chunk.Index, chunkHash);
        if (cache.Chunks.TryGetValue(chunkKey, out var cachedChunk) && string.Equals(cachedChunk.ContentHash, chunkHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"CHUNK CACHE HIT [{extension}] FILE {fileNumber}/{fileCount} CHUNK {chunk.Index}/{chunk.Count}");
            return cachedChunk.Analysis;
        }

        var prompt = BuildPrompt(_workspaceRoot, extension, fileNumber, fileCount, file, chunk.Content, runId, chunk.Index, chunk.Count, chunk.StartLine, chunk.EndLine);
        Console.WriteLine($"OLLAMA REQUEST [{extension}] FILE {fileNumber}/{fileCount} CHUNK {chunk.Index}/{chunk.Count}");
        Console.WriteLine("----------------------------------------------");
        Console.WriteLine($"Request estimate: ~{prompt.Length} chars");
        Console.WriteLine($"Chunk line range: {chunk.StartLine}-{chunk.EndLine}");
        Console.WriteLine(prompt);
        Console.WriteLine();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await AskOllamaStreamingAsync(prompt);
            if (TryNormalizeSingleFileDump(response, runId, _workspaceRoot, fileCount, fileNumber, file, extension, out var analysis))
            {
                cache.Chunks[chunkKey] = new CachedChunkAnalysis(chunkHash, analysis);
                await SaveCacheAsync(cache);
                return analysis;
            }

            Console.WriteLine($"Placeholder or empty response for chunk {chunk.Index}/{chunk.Count}. attempt {attempt}");
            Console.WriteLine();
        }

        throw new InvalidOperationException($"Ollama returned empty content for {extension} file {fileNumber}/{fileCount} chunk {chunk.Index}/{chunk.Count}.");
    }

    private static void AppendListBlock(StringBuilder sb, string label, IReadOnlyList<string> values)
    {
        sb.AppendLine(label);
        if (values.Count == 0)
        {
            sb.AppendLine("- unknown");
            return;
        }

        foreach (var value in values)
        {
            sb.AppendLine($"- {value}");
        }
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

    private static string? ReadBlock(string content, string label)
    {
        var marker = $"{label}<<BLOCK";
        var start = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var blockStart = start + marker.Length;
        if (blockStart >= content.Length)
        {
            return null;
        }

        var end = content.IndexOf("\nBLOCK", blockStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = content.IndexOf("\r\nBLOCK", blockStart, StringComparison.OrdinalIgnoreCase);
        }

        if (end < 0)
        {
            return null;
        }

        var block = content[blockStart..end].Trim().Trim('\r', '\n');
        return string.IsNullOrWhiteSpace(block) ? null : block;
    }

    private static IReadOnlyList<string> ReadList(string content, string label)
    {
        var values = new List<string>();
        var marker = label + ":";
        var inSection = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().Trim('\r');
            if (!inSection)
            {
                if (line.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                    line.Equals(marker, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                }
                continue;
            }

            if (line.StartsWith("@@") ||
                line.EndsWith("<<BLOCK", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(line, @"^[A-Z0-9_]+:\s*"))
            {
                break;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var value = line[2..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static int GetChunkSizeForExtension(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "cs" or "cpp" or "cc" or "cxx" or "c" or "h" or "hpp" or "hh" or "inl" => 3600,
            "py" or "js" or "ts" or "tsx" or "jsx" => 3000,
            "json" or "md" or "txt" or "yml" or "yaml" or "xml" or "toml" => 1400,
            "cmake" or "bat" or "cmd" or "ps1" or "sh" => 1800,
            _ => MaxChunkContentChars
        };
    }

    private IEnumerable<FileChunk> ReadOriginFileChunks(string relativePath, int chunkSize)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_originRoot, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_originRoot), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!File.Exists(fullPath))
        {
            return [];
        }

        var content = File.ReadAllText(fullPath, Encoding.UTF8);
        if (content.Length == 0)
        {
            return [new FileChunk(1, 1, "unknown", 1, 1)];
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var chunks = new List<FileChunk>();
        var buffer = new StringBuilder();
        var chunkIndex = 1;
        var lineNumber = 1;
        var chunkStartLine = 1;

        foreach (var line in lines)
        {
            if (buffer.Length > 0 && buffer.Length + line.Length + 1 > chunkSize)
            {
                chunks.Add(new FileChunk(chunkIndex++, 0, buffer.ToString().TrimEnd(), chunkStartLine, lineNumber - 1));
                buffer.Clear();
                chunkStartLine = lineNumber;
            }

            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.Append(line);
            lineNumber++;
        }

        if (buffer.Length > 0)
        {
            chunks.Add(new FileChunk(chunkIndex, 0, buffer.ToString().TrimEnd(), chunkStartLine, lineNumber - 1));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(new FileChunk(1, 1, content, 1, lines.Length));
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i] = chunks[i] with { Count = chunks.Count };
        }

        return chunks;
    }

    private static string BuildCacheKey(string extension, string filePath, string contentHash)
    {
        return $"{extension}|{filePath}|{contentHash}";
    }

    private static string BuildChunkCacheKey(string extension, string filePath, string fileHash, int chunkIndex, string chunkHash)
    {
        return $"{extension}|{filePath}|{fileHash}|chunk:{chunkIndex}|{chunkHash}";
    }

    private static string ComputeFileHash(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return "missing";
        }

        using var stream = File.OpenRead(fullPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string ComputeChunkHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private AnalysisCacheStore LoadCache()
    {
        if (!File.Exists(_cachePath))
        {
            return new AnalysisCacheStore();
        }

        try
        {
            var json = File.ReadAllText(_cachePath, Encoding.UTF8);
            var cache = JsonSerializer.Deserialize<AnalysisCacheStore>(json);
            return cache ?? new AnalysisCacheStore();
        }
        catch
        {
            return new AnalysisCacheStore();
        }
    }

    private async Task SaveCacheAsync(AnalysisCacheStore cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        await WriteAtomicAsync(_cachePath, json);
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
            return;
        }

        File.Move(tempPath, path);
    }

    private static bool ContainsPlaceholderTokens(string content)
    {
        return content.Contains("<unknown>", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<multiline text>", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<enum>", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("@@BEGIN_FILE@@", StringComparison.OrdinalIgnoreCase) &&
               content.Contains("FILE_PATH:", StringComparison.OrdinalIgnoreCase) &&
               content.Contains("FUNCTIONAL_SUMMARY<<BLOCK", StringComparison.OrdinalIgnoreCase) &&
               (content.Contains("- <item>", StringComparison.OrdinalIgnoreCase) || content.Contains("- unknown", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlaceholderResponse(string content)
    {
        return string.IsNullOrWhiteSpace(content) ||
               content.Contains("<unknown>", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<multiline text>", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConfidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "medium";
        }

        var cleaned = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z]", string.Empty);
        return cleaned switch
        {
            "high" => "high",
            "medium" => "medium",
            "med" => "medium",
            "low" => "low",
            _ => "medium"
        };
    }

    private static string NormalizeEnum(string? value, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowed.Contains("unknown", StringComparer.OrdinalIgnoreCase) ? "unknown" : allowed[0];
        }

        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z]", string.Empty);
        foreach (var item in allowed)
        {
            if (string.Equals(normalized, Regex.Replace(item.ToLowerInvariant(), @"[^a-z]", string.Empty), StringComparison.Ordinal))
            {
                return item;
            }
        }

        return allowed.Contains("unknown", StringComparer.OrdinalIgnoreCase) ? "unknown" : allowed[0];
    }

    private static string InferLanguageFromExtension(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "cs" => "csharp",
            "cpp" or "cc" or "cxx" or "hpp" or "h" => "cpp",
            "py" => "python",
            "js" => "javascript",
            "ts" => "typescript",
            "java" => "java",
            "rs" => "rust",
            "go" => "go",
            "c" => "c",
            "sh" or "bash" => "shell",
            "ps1" => "shell",
            _ => "unknown"
        };
    }

    private static string InferArchComponentFromExtension(string extension)
    {
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "cs" or "py" or "js" or "ts" or "java" or "rs" or "go" => "application_service",
            "c" or "cpp" or "cc" or "cxx" or "h" or "hpp" => "domain_module",
            "bat" or "sh" or "ps1" => "cli_tool",
            "cmake" or "txt" or "md" or "yml" or "yaml" or "json" => "infra_config",
            _ => "unknown"
        };
    }

    private async Task WarmupAsync()
    {
        var payload = new
        {
            model = _model,
            prompt = "ANALYSIS_DUMP_VERSION: 1\nRUN_ID: warmup\nROOT_PATH: warmup\nFILE_COUNT: 1\n@@BEGIN_FILE@@\nFILE_INDEX: 1\nFILE_PATH: warmup\nLANGUAGE: unknown\nARCH_COMPONENT: unknown\nARCH_COMPONENT_CONFIDENCE: medium\nFUNCTIONAL_SUMMARY<<BLOCK\nunknown\nBLOCK\nTECHNICAL_SUMMARY<<BLOCK\nunknown\nBLOCK\nINPUTS:\n- unknown\nOUTPUTS:\n- unknown\nSIDE_EFFECTS:\n- unknown\nPRIMARY_DEPENDENCIES:\n- unknown\nCONTROL_FLOW_COMPLEXITY: unknown\nSTATE_COMPLEXITY: unknown\nDATA_COMPLEXITY: unknown\nDEPENDENCY_COMPLEXITY: unknown\nOPERATIONAL_COMPLEXITY: unknown\nALGORITHMIC_COMPLEXITY: unknown\nEXECUTION_PROFILE:\n- unknown\nCPU_UNDERUTILIZATION_SIGNALS:\n- unknown\nWAITING_PATTERNS:\n- unknown\nBOTTLENECK_CANDIDATES:\n- unknown\nUOPS_PLAUSIBILITY<<BLOCK\nunknown\nBLOCK\nEVIDENCE:\n- unknown\nRISKS:\n- unknown\nUNCERTAINTIES:\n- unknown\n@@END_FILE@@\nEND_ANALYSIS_DUMP",
            stream = false,
            think = false,
            options = new
            {
                temperature = 0,
                top_p = 1,
                num_predict = 24,
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", payload);
        response.EnsureSuccessStatusCode();
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
                num_predict = _step3PredictTokens,
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

        heartbeatCts.Cancel();
        try
        {
            await heartbeat;
        }
        catch (TaskCanceledException)
        {
        }

        Console.WriteLine();
        Console.WriteLine($"completed in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"stream flags: thinking={(sawThinking ? "yes" : "no")}, response={(sawResponse ? "yes" : "no")}");

        var content = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            var preview = rawPreview.Count > 0 ? string.Join(" || ", rawPreview) : "(no stream lines)";
            throw new InvalidOperationException($"Ollama returned empty content. Done={sawDone}. Preview: {preview}");
        }

        return content;
    }

    private static string BuildReportFileName(string extension, IReadOnlyList<string> files, string runId)
    {
        var ext = SanitizeFileName(extension.TrimStart('.'));
        var firstSlug = files.Count > 0 ? Slugify(Path.GetFileNameWithoutExtension(files[0])) : "analysis";
        var lastSlug = files.Count > 1 ? Slugify(Path.GetFileNameWithoutExtension(files[^1])) : firstSlug;
        var runSlug = SanitizeFileName(runId);
        var parts = new[] { ext, firstSlug, lastSlug, runSlug }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var name = string.Join("--", parts);
        if (name.Length > MaxReportFileNameChars - 3)
        {
            var keep = Math.Max(1, MaxReportFileNameChars - 3);
            name = name[..keep];
        }

        return $"{name}.md";
    }

    private static string BuildReportIndex(IEnumerable<ReportEntry> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Extension Analysis Reports");
        sb.AppendLine();
        sb.AppendLine($"- Generated: `{DateTimeOffset.Now:O}`");
        sb.AppendLine($"- Source index: `generated/originfileindex.md`");
        sb.AppendLine();
        sb.AppendLine("## Reports");
        sb.AppendLine();
        sb.AppendLine("| Extension | Files | Report |");
        sb.AppendLine("| --- | ---: | --- |");

        foreach (var report in reports.OrderBy(x => x.Extension, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{report.Extension}` | {report.FileCount} | `{report.ReportFileName}` |");
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        var chars = value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned.Trim('_');
    }

    private static string Slugify(string value)
    {
        var cleaned = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "analysis" : cleaned;
    }

    private static FileAnalysis ParseAnalysis(string response, string filePath, string extension)
    {
        var normalized = response.Trim();
        var language = ReadLabel(normalized, "LANGUAGE") ?? InferLanguageFromExtension(extension);
        var archComponent = ReadLabel(normalized, "ARCH_COMPONENT") ?? InferArchComponentFromExtension(extension);
        var archConfidence = NormalizeEnum(ReadLabel(normalized, "ARCH_COMPONENT_CONFIDENCE"), "medium", "low", "medium", "high");
        var functionalSummary = ReadBlock(normalized, "FUNCTIONAL_SUMMARY") ?? "unknown";
        var technicalSummary = ReadBlock(normalized, "TECHNICAL_SUMMARY") ?? "unknown";
        var inputs = ReadList(normalized, "INPUTS");
        var outputs = ReadList(normalized, "OUTPUTS");
        var sideEffects = ReadList(normalized, "SIDE_EFFECTS");
        var dependencies = ReadList(normalized, "PRIMARY_DEPENDENCIES");
        var controlFlow = NormalizeEnum(ReadLabel(normalized, "CONTROL_FLOW_COMPLEXITY"), "unknown", "low", "medium", "high");
        var stateComplexity = NormalizeEnum(ReadLabel(normalized, "STATE_COMPLEXITY"), "unknown", "low", "medium", "high");
        var dataComplexity = NormalizeEnum(ReadLabel(normalized, "DATA_COMPLEXITY"), "unknown", "low", "medium", "high");
        var dependencyComplexity = NormalizeEnum(ReadLabel(normalized, "DEPENDENCY_COMPLEXITY"), "unknown", "low", "medium", "high");
        var operationalComplexity = NormalizeEnum(ReadLabel(normalized, "OPERATIONAL_COMPLEXITY"), "unknown", "low", "medium", "high");
        var algorithmicComplexity = NormalizeEnum(ReadLabel(normalized, "ALGORITHMIC_COMPLEXITY"), "unknown", "low", "medium", "high");
        var execProfile = ReadList(normalized, "EXECUTION_PROFILE");
        var underutilization = ReadList(normalized, "CPU_UNDERUTILIZATION_SIGNALS");
        var waitingPatterns = ReadList(normalized, "WAITING_PATTERNS");
        var bottlenecks = ReadList(normalized, "BOTTLENECK_CANDIDATES");
        var uops = ReadBlock(normalized, "UOPS_PLAUSIBILITY") ?? "unknown";
        var evidence = ReadList(normalized, "EVIDENCE");
        var risks = ReadList(normalized, "RISKS");
        var uncertainties = ReadList(normalized, "UNCERTAINTIES");
        var auditNotes = ReadLabel(normalized, "AUDIT_NOTES") ?? "unknown";
        var auditLocations = ReadList(normalized, "AUDIT_LOCATIONS");

        return new FileAnalysis(
            filePath,
            language,
            archComponent,
            archConfidence,
            functionalSummary,
            technicalSummary,
            inputs,
            outputs,
            sideEffects,
            dependencies,
            controlFlow,
            stateComplexity,
            dataComplexity,
            dependencyComplexity,
            operationalComplexity,
            algorithmicComplexity,
            execProfile,
            underutilization,
            waitingPatterns,
            bottlenecks,
            uops,
            evidence,
            risks,
            uncertainties,
            auditNotes,
            auditLocations);
    }

    private static string BuildFinalRecord(int fileIndex, string filePath, IReadOnlyList<FileAnalysis> analyses)
    {
        var merged = MergeAnalyses(filePath, analyses);
        var sb = new StringBuilder();
        sb.AppendLine("@@BEGIN_FILE@@");
        sb.AppendLine($"FILE_INDEX: {fileIndex}");
        sb.AppendLine($"FILE_PATH: {merged.Path}");
        sb.AppendLine($"LANGUAGE: {merged.Language}");
        sb.AppendLine($"ARCH_COMPONENT: {merged.ArchComponent}");
        sb.AppendLine($"ARCH_COMPONENT_CONFIDENCE: {merged.ArchComponentConfidence}");
        sb.AppendLine("FUNCTIONAL_SUMMARY<<BLOCK");
        sb.AppendLine(merged.FunctionalSummary);
        sb.AppendLine("BLOCK");
        sb.AppendLine("TECHNICAL_SUMMARY<<BLOCK");
        sb.AppendLine(merged.TechnicalSummary);
        sb.AppendLine("BLOCK");
        AppendListBlock(sb, "INPUTS:", merged.Inputs);
        AppendListBlock(sb, "OUTPUTS:", merged.Outputs);
        AppendListBlock(sb, "SIDE_EFFECTS:", merged.SideEffects);
        AppendListBlock(sb, "PRIMARY_DEPENDENCIES:", merged.PrimaryDependencies);
        sb.AppendLine($"CONTROL_FLOW_COMPLEXITY: {merged.ControlFlowComplexity}");
        sb.AppendLine($"STATE_COMPLEXITY: {merged.StateComplexity}");
        sb.AppendLine($"DATA_COMPLEXITY: {merged.DataComplexity}");
        sb.AppendLine($"DEPENDENCY_COMPLEXITY: {merged.DependencyComplexity}");
        sb.AppendLine($"OPERATIONAL_COMPLEXITY: {merged.OperationalComplexity}");
        sb.AppendLine($"ALGORITHMIC_COMPLEXITY: {merged.AlgorithmicComplexity}");
        AppendListBlock(sb, "EXECUTION_PROFILE:", merged.ExecutionProfile);
        AppendListBlock(sb, "CPU_UNDERUTILIZATION_SIGNALS:", merged.CpuUnderutilizationSignals);
        AppendListBlock(sb, "WAITING_PATTERNS:", merged.WaitingPatterns);
        AppendListBlock(sb, "BOTTLENECK_CANDIDATES:", merged.BottleneckCandidates);
        sb.AppendLine("UOPS_PLAUSIBILITY<<BLOCK");
        sb.AppendLine(merged.UopsPlausibility);
        sb.AppendLine("BLOCK");
        AppendListBlock(sb, "EVIDENCE:", merged.Evidence);
        AppendListBlock(sb, "RISKS:", merged.Risks);
        AppendListBlock(sb, "UNCERTAINTIES:", merged.Uncertainties);
        sb.AppendLine("AUDIT_NOTES<<BLOCK");
        sb.AppendLine(merged.AuditNotes);
        sb.AppendLine("BLOCK");
        AppendListBlock(sb, "AUDIT_LOCATIONS:", merged.AuditLocations);
        sb.AppendLine("@@END_FILE@@");
        return sb.ToString();
    }

    private static FileAnalysis MergeAnalyses(string filePath, IReadOnlyList<FileAnalysis> analyses)
    {
        string PickString(Func<FileAnalysis, string> selector, string fallback) =>
            analyses.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "unknown") ?? fallback;

        IReadOnlyList<string> MergeLists(Func<FileAnalysis, IReadOnlyList<string>> selector)
        {
            var merged = new List<string>();
            foreach (var item in analyses.SelectMany(selector))
            {
                if (!string.IsNullOrWhiteSpace(item) && !merged.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(item);
                }
            }

            return merged.Count > 0 ? merged : ["unknown"];
        }

        string MergeComplexity(Func<FileAnalysis, string> selector)
        {
            var values = analyses
                .Select(selector)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Contains("high", StringComparer.OrdinalIgnoreCase))
            {
                return "high";
            }

            if (values.Contains("medium", StringComparer.OrdinalIgnoreCase))
            {
                return "medium";
            }

            if (values.Contains("low", StringComparer.OrdinalIgnoreCase))
            {
                return "low";
            }

            return "unknown";
        }

        string MergeText(Func<FileAnalysis, string> selector)
        {
            var items = analyses
                .Select(selector)
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (items.Count == 0)
            {
                return "unknown";
            }

            if (items.Count == 1)
            {
                return items[0];
            }

            return string.Join("; ", items.Take(3));
        }

        return new FileAnalysis(
            filePath,
            PickString(a => a.Language, InferLanguageFromExtension(Path.GetExtension(filePath))),
            PickString(a => a.ArchComponent, InferArchComponentFromExtension(Path.GetExtension(filePath))),
            PickString(a => a.ArchComponentConfidence, "medium"),
            MergeText(a => a.FunctionalSummary),
            MergeText(a => a.TechnicalSummary),
            MergeLists(a => a.Inputs),
            MergeLists(a => a.Outputs),
            MergeLists(a => a.SideEffects),
            MergeLists(a => a.PrimaryDependencies),
            MergeComplexity(a => a.ControlFlowComplexity),
            MergeComplexity(a => a.StateComplexity),
            MergeComplexity(a => a.DataComplexity),
            MergeComplexity(a => a.DependencyComplexity),
            MergeComplexity(a => a.OperationalComplexity),
            MergeComplexity(a => a.AlgorithmicComplexity),
            MergeLists(a => a.ExecutionProfile),
            MergeLists(a => a.CpuUnderutilizationSignals),
            MergeLists(a => a.WaitingPatterns),
            MergeLists(a => a.BottleneckCandidates),
            MergeText(a => a.UopsPlausibility),
            MergeLists(a => a.Evidence),
            MergeLists(a => a.Risks),
            MergeLists(a => a.Uncertainties),
            MergeText(a => a.AuditNotes),
            MergeLists(a => a.AuditLocations));
    }

    private sealed record ReportEntry(string Extension, string ReportFileName, int FileCount);

    private sealed record ExtensionGroup(string Extension, int Count, string RelativeMarkdownPath, IReadOnlyList<string> Files);

    private sealed record FileChunk(int Index, int Count, string Content, int StartLine, int EndLine);

    private sealed record CachedFileAnalysis(string ContentHash, string Record);

    private sealed record CachedChunkAnalysis(string ContentHash, FileAnalysis Analysis);

    private sealed record FileRecordResult(int Index, string Record);

    private sealed class AnalysisCacheStore
    {
        public Dictionary<string, CachedFileAnalysis> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CachedChunkAnalysis> Chunks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FileAnalysis(
        string Path,
        string Language,
        string ArchComponent,
        string ArchComponentConfidence,
        string FunctionalSummary,
        string TechnicalSummary,
        IReadOnlyList<string> Inputs,
        IReadOnlyList<string> Outputs,
        IReadOnlyList<string> SideEffects,
        IReadOnlyList<string> PrimaryDependencies,
        string ControlFlowComplexity,
        string StateComplexity,
        string DataComplexity,
        string DependencyComplexity,
        string OperationalComplexity,
        string AlgorithmicComplexity,
        IReadOnlyList<string> ExecutionProfile,
        IReadOnlyList<string> CpuUnderutilizationSignals,
        IReadOnlyList<string> WaitingPatterns,
        IReadOnlyList<string> BottleneckCandidates,
        string UopsPlausibility,
        IReadOnlyList<string> Evidence,
        IReadOnlyList<string> Risks,
        IReadOnlyList<string> Uncertainties,
        string AuditNotes,
        IReadOnlyList<string> AuditLocations);

    private sealed class ExtensionGroupBuilder
    {
        public ExtensionGroupBuilder(string extension)
        {
            Extension = extension;
        }

        public string Extension { get; }
        public string MarkdownPath { get; set; } = string.Empty;
        public List<string> Files { get; } = new();

        public ExtensionGroup Build() => new(Extension, Files.Count, MarkdownPath, Files.ToList());
    }
}
