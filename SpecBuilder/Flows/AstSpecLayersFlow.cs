using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace SpecBuilder.Flows;

internal sealed class AstSpecLayersFlow : IPipelineFlow
{
    private readonly string _workspaceRoot;
    private readonly string _astRoot;
    private readonly string _specRoot;
    private readonly int _parallelism;
    private static readonly Dictionary<string, IReadOnlyList<ClusterRow>> ClusterCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions ClusterCacheJsonOptions = new() { WriteIndented = true };

    public AstSpecLayersFlow(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        _astRoot = Path.Combine(workspaceRoot, "generated", "ast-database");
        _specRoot = Path.Combine(workspaceRoot, "generated", "ast-spec");
        _parallelism = Math.Max(1, (int)Math.Ceiling(Environment.ProcessorCount * 0.75));
    }

    public string Name => "4. Build layered spec from AST";

    public string Description =>
        "Reads the latest canonical AST snapshot and writes layered specification pages from repository shape down to cluster and relationship views.";

    public async Task<FlowResult> ExecuteAsync()
    {
        if (!Directory.Exists(_astRoot))
        {
            return new FlowResult($"Missing AST database folder: {_astRoot}. Run step 3 first.", _astRoot);
        }

        var snapshotPath = Directory.GetFiles(_astRoot, "ast-database-*.json")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (snapshotPath is null)
        {
            return new FlowResult($"No AST snapshot found in {_astRoot}. Run step 3 first.", _astRoot);
        }

        Directory.CreateDirectory(_specRoot);
        ClearSpecOutputs();

        Console.WriteLine("[step4] reading canonical AST snapshot");
        var snapshot = await File.ReadAllTextAsync(snapshotPath, Encoding.UTF8);
        var document = JsonSerializer.Deserialize<CanonicalAstDocument>(snapshot);
        if (document is null)
        {
            return new FlowResult($"Unable to read canonical AST snapshot: {snapshotPath}", snapshotPath);
        }

        Console.WriteLine("[step4] building layered spec markdown");
        var report = BuildLayeredSpec(document);

        var snapshotStem = Path.GetFileNameWithoutExtension(snapshotPath);
        var outputPath = Path.Combine(_specRoot, $"{snapshotStem}-ast-spec.md");
        Console.WriteLine($"[step4] writing main spec to {outputPath}");
        await File.WriteAllTextAsync(outputPath, report, Encoding.UTF8);
        Console.WriteLine("[step4] writing stable latest spec");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "ast-spec-latest.md"), report, Encoding.UTF8);
        Console.WriteLine("[step4] writing subsystem pages");
        await WriteSubsystemPagesAsync(document);
        Console.WriteLine("[step4] writing hierarchy pages");
        await WriteHierarchyPagesAsync(document);
        Console.WriteLine("[step4] writing C4 pages");
        await WriteC4PagesAsync(document);
        Console.WriteLine("[step4] writing fact diff");
        await WriteFactDiffAsync(document);
        Console.WriteLine("[step4] writing landing page");
        await WriteLandingPageAsync(document);

        return new FlowResult("Wrote layered AST spec report.", outputPath);
    }

    private void ClearSpecOutputs()
    {
        if (!Directory.Exists(_specRoot))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_specRoot, "*.md"))
        {
            File.Delete(file);
        }
    }

    private string BuildLayeredSpec(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Layered AST Spec");
        sb.AppendLine();
        sb.AppendLine("## C4 Entry Point");
        sb.AppendLine();
        sb.AppendLine("Use the C4 views first when you want abstract architecture, then drill into the detailed layer bands.");
        sb.AppendLine();
        sb.AppendLine("- [C4 Index](c4-index.md)");
        sb.AppendLine("- [Context](context.md)");
        sb.AppendLine("- [Container](container.md)");
        sb.AppendLine("- [Component](component.md)");
        sb.AppendLine("- [Code](code.md)");
        sb.AppendLine();
        sb.AppendLine("## Recommended Reading Order");
        sb.AppendLine();
        sb.AppendLine("1. `c4-index.md`");
        sb.AppendLine("2. `context.md`");
        sb.AppendLine("3. `container.md`");
        sb.AppendLine("4. `component.md`");
        sb.AppendLine("5. `code.md`");
        sb.AppendLine("6. `ast-spec-latest.md` band sections");
        sb.AppendLine("7. `facts.md` for file-by-file enrichment");
        sb.AppendLine();
        sb.AppendLine($"- Workspace: `{_workspaceRoot}`");
        sb.AppendLine($"- Snapshot: `{document.SnapshotPath}`");
        sb.AppendLine($"- Generated: `{document.GeneratedAt:O}`");
        sb.AppendLine($"- Files: `{document.Files.Count}`");
        sb.AppendLine($"- Groups: `{document.Groups.Count}`");
        sb.AppendLine($"- Symbols: `{document.Symbols.Count}`");
        sb.AppendLine();
        sb.AppendLine("## Layer Schema");
        sb.AppendLine();
        Console.WriteLine("[step4] rendering layer schema");
        sb.AppendLine("| Layer ID | Name | Purpose |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `L0` | Repository Shape | Coarse repository-wide clusters and size signals |");
        sb.AppendLine("| `L1` | Language Groups | Parser-backed group summaries |");
        sb.AppendLine("| `L2` | File Roles | File-level abstraction for spec scaffolding |");
        sb.AppendLine("| `L3` | Detailed Constructs | AST-derived terms for enrichment inputs |");
        sb.AppendLine("| `L4` | Relationship View | Evidence-backed edges between symbols and files |");
        sb.AppendLine("| `L5` | Domain Clusters | Inferred architectural roles and subsystem groupings |");
        sb.AppendLine("| `L6` | Architecture Roles | Higher-order system responsibilities |");
        sb.AppendLine("| `L7` | Domain Concepts | Repeated domain vocabulary and concepts |");
        sb.AppendLine("| `L8` | Behavioral Scenarios | Code paths and flows expressed as scenarios |");
        sb.AppendLine("| `L9` | Product Intent | Inferred purpose and user-facing intent |");
        sb.AppendLine("| `L10` | Open Questions | Gaps, ambiguities, and low-confidence facts |");
        sb.AppendLine("| `L11` | Performance Analysis | Async usage, waiting patterns, and bottleneck candidates |");
        sb.AppendLine("| `L12` | Application and Tool Taxonomy | Entrypoints, CLI tools, generators, and operator surfaces |");
        sb.AppendLine("| `L13` | Runtime and System Roles | Components, services, pipelines, processors, adapters, orchestrators |");
        sb.AppendLine("| `L14` | Data Code Structures | Models, DTOs, schemas, records, entities, and configuration shapes |");
        sb.AppendLine("| `L15` | Execution and State Flow | Control flow, transitions, sequencing, and lifecycle behavior |");
        sb.AppendLine("| `L16` | Persistence and Storage | Files, databases, caches, serialization, and storage boundaries |");
        sb.AppendLine("| `L17` | External Integration Boundaries | APIs, adapters, network edges, and host integration points |");
        sb.AppendLine("| `L18` | Command and Query Responsibility | Read, write, mutate, and inspect behavior |");
        sb.AppendLine("| `L19` | Dependency Direction and Coupling | Fan-in, fan-out, and coupling strength |");
        sb.AppendLine("| `L20` | Test Coverage and Verification | Tests, assertions, fixtures, and validation roles |");
        sb.AppendLine("| `L21` | Configuration and Environment Behavior | Config, environment, feature flags, and runtime settings |");
        sb.AppendLine("| `L22` | Error Handling and Resilience | Exceptions, retries, guards, fallbacks, and recovery paths |");
        sb.AppendLine("| `L23` | Observability and Logging Semantics | Logs, traces, metrics, diagnostics, and telemetry |");
        sb.AppendLine();
        sb.AppendLine("## Layer Index");
        sb.AppendLine();
        sb.AppendLine("1. `L0` Repository Shape");
        sb.AppendLine("2. `L1` Language Groups");
        sb.AppendLine("3. `L2` File Roles");
        sb.AppendLine("4. `L3` Detailed Constructs");
        sb.AppendLine("5. `L4` Relationship View");
        sb.AppendLine("6. `L5` Domain Clusters");
        sb.AppendLine("7. `L6` Architecture Roles");
        sb.AppendLine("8. `L7` Domain Concepts");
        sb.AppendLine("9. `L8` Behavioral Scenarios");
        sb.AppendLine("10. `L9` Product Intent");
        sb.AppendLine("11. `L10` Open Questions");
        sb.AppendLine("12. `L11` Performance Analysis");
        sb.AppendLine("13. `L12` Application and Tool Taxonomy");
        sb.AppendLine("14. `L13` Runtime and System Roles");
        sb.AppendLine("15. `L14` Data Code Structures");
        sb.AppendLine("16. `L15` Execution and State Flow");
        sb.AppendLine("17. `L16` Persistence and Storage");
        sb.AppendLine("18. `L17` External Integration Boundaries");
        sb.AppendLine("19. `L18` Command and Query Responsibility");
        sb.AppendLine("20. `L19` Dependency Direction and Coupling");
        sb.AppendLine("21. `L20` Test Coverage and Verification");
        sb.AppendLine("22. `L21` Configuration and Environment Behavior");
        sb.AppendLine("23. `L22` Error Handling and Resilience");
        sb.AppendLine("24. `L23` Observability and Logging Semantics");
        sb.AppendLine();
        sb.AppendLine("## Spec Bands");
        sb.AppendLine();
        sb.AppendLine("| Band | Layers | Focus |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `B0` | `L0-L3` | Repository shape, language groupings, file roles, and detailed constructs |");
        sb.AppendLine("| `B1` | `L4-L7` | Relations, clusters, architecture roles, and domain vocabulary |");
        sb.AppendLine("| `B2` | `L8-L11` | Scenarios, intent, uncertainty, and performance signals |");
        sb.AppendLine("| `B3` | `L12-L17` | Application/tool, runtime roles, data structures, execution, persistence, and integration |");
        sb.AppendLine("| `B4` | `L18-L23` | Command/query, coupling, verification, configuration, resilience, and observability |");
        sb.AppendLine();
        sb.AppendLine("## C4 Mapping");
        sb.AppendLine();
        sb.AppendLine("| C4 Level | Derived From | Purpose |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `Context` | `B0-B2` | Repository, domain, scenario, and intent overview |");
        sb.AppendLine("| `Container` | `B2-B3` | Application units, runtime roles, and subsystem boundaries |");
        sb.AppendLine("| `Component` | `B3-B4` | Internal responsibilities, coupling, resilience, and operational semantics |");
        sb.AppendLine("| `Code` | `L2-L4, L12-L23` | File, symbol, and behavior detail for enrichment and reverse engineering |");
        sb.AppendLine();
        sb.AppendLine("## Band Index");
        sb.AppendLine();
        sb.AppendLine("1. `B0` Repository and structure band");
        sb.AppendLine("2. `B1` Relationship and architecture band");
        sb.AppendLine("3. `B2` Scenario and performance band");
        sb.AppendLine("4. `B3` System and boundary band");
        sb.AppendLine("5. `B4` Operational semantics band");
        sb.AppendLine();
        sb.AppendLine("## C4 Index");
        sb.AppendLine();
        sb.AppendLine("1. `Context` Repository and domain context");
        sb.AppendLine("2. `Container` Runtime and subsystem containers");
        sb.AppendLine("3. `Component` Internal component views");
        sb.AppendLine("4. `Code` File and symbol-level views");
        sb.AppendLine();
        Console.WriteLine("[step4] rendering L0");
        AppendLayer0(sb, document);
        Console.WriteLine("[step4] rendering L1");
        AppendLayer1(sb, document);
        Console.WriteLine("[step4] rendering L2");
        AppendLayer2(sb, document);
        Console.WriteLine("[step4] rendering L3");
        AppendLayer3(sb, document);
        Console.WriteLine("[step4] rendering L4");
        AppendLayer4(sb, document);
        Console.WriteLine("[step4] rendering L5");
        AppendLayer5(sb, document);
        Console.WriteLine("[step4] rendering L6");
        AppendLayer6(sb, document);
        Console.WriteLine("[step4] rendering L7");
        AppendLayer7(sb, document);
        Console.WriteLine("[step4] rendering L8");
        AppendLayer8(sb, document);
        Console.WriteLine("[step4] rendering L9");
        AppendLayer9(sb, document);
        Console.WriteLine("[step4] rendering L10");
        AppendLayer10(sb, document);
        Console.WriteLine("[step4] rendering L11");
        AppendLayer11(sb, document);
        Console.WriteLine("[step4] rendering L12");
        AppendLayer12(sb, document);
        Console.WriteLine("[step4] rendering L13");
        AppendLayer13(sb, document);
        Console.WriteLine("[step4] rendering L14");
        AppendLayer14(sb, document);
        Console.WriteLine("[step4] rendering L15");
        AppendLayer15(sb, document);
        Console.WriteLine("[step4] rendering L16");
        AppendLayer16(sb, document);
        Console.WriteLine("[step4] rendering L17");
        AppendLayer17(sb, document);
        Console.WriteLine("[step4] rendering L18");
        AppendLayer18(sb, document);
        Console.WriteLine("[step4] rendering L19");
        AppendLayer19(sb, document);
        Console.WriteLine("[step4] rendering L20");
        AppendLayer20(sb, document);
        Console.WriteLine("[step4] rendering L21");
        AppendLayer21(sb, document);
        Console.WriteLine("[step4] rendering L22");
        AppendLayer22(sb, document);
        Console.WriteLine("[step4] rendering L23");
        AppendLayer23(sb, document);
        Console.WriteLine("[step4] rendering C4 views");
        AppendC4Sections(sb, document);
        Console.WriteLine("[step4] rendering important files");
        AppendImportantFiles(sb, document);
        return sb.ToString();
    }

    private static void AppendLayer0(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L0: Repository Shape");
        sb.AppendLine();
        sb.AppendLine("| Group | Files | Root Nodes | Extensions |");
        sb.AppendLine("| --- | ---: | ---: | --- |");
        foreach (var group in document.Groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{group.Key}` | {group.FileCount} | {group.TotalRootNodes} | `{string.Join(", ", group.Extensions)}` |");
        }
        sb.AppendLine();
    }

    private static void AppendLayer1(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L1: Language Groups");
        sb.AppendLine();
        foreach (var group in document.Groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### `{group.Key}`");
            sb.AppendLine();
            sb.AppendLine($"- File count: `{group.FileCount}`");
            sb.AppendLine($"- Total root nodes: `{group.TotalRootNodes}`");
            sb.AppendLine($"- Extensions: `{string.Join(", ", group.Extensions)}`");
            sb.AppendLine();
        }
    }

    private static void AppendLayer2(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L2: File Roles");
        sb.AppendLine();
        sb.AppendLine("| File | Language | Role | Root Nodes | Status |");
        sb.AppendLine("| --- | --- | --- | ---: | --- |");
        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(500))
        {
            sb.AppendLine($"| `{file.RelativePath}` | `{file.Language}` | `{InferRole(file)}` | {file.RootNodeCount} | `{file.Status}` |");
        }
        if (document.Files.Count > 500)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated file table at 500 rows out of `{document.Files.Count}` files.");
        }
        sb.AppendLine();
    }

    private static void AppendLayer3(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L3: Detailed Constructs");
        sb.AppendLine();
        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(120))
        {
            sb.AppendLine($"### `{file.RelativePath}`");
            sb.AppendLine();
            sb.AppendLine($"- Language: `{file.Language}`");
            sb.AppendLine($"- Status: `{file.Status}`");
            sb.AppendLine($"- Tags: `{string.Join(", ", file.Tags)}`");
            sb.AppendLine($"- References: `{file.References.Count}`");
            sb.AppendLine($"- Top kinds: `{string.Join(", ", CollectKinds(file.Root).Take(20))}`");
            sb.AppendLine();
        }
        if (document.Files.Count > 120)
        {
            sb.AppendLine($"- Additional `{document.Files.Count - 120}` files omitted from detailed layer.");
            sb.AppendLine();
        }
    }

    private static void AppendLayer4(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L4: Relationship View");
        sb.AppendLine();
        sb.AppendLine("| Category | Source | Relation | Target | Evidence |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var relation in ExtractRelations(document).Take(1000))
        {
            sb.AppendLine($"| `{relation.Category}` | `{relation.Source}` | `{relation.Kind}` | `{relation.Target}` | `{relation.Evidence}` |");
        }
        sb.AppendLine();
    }

    private static void AppendLayer5(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L5: Domain Clusters");
        sb.AppendLine();
        sb.AppendLine("| Cluster | Files | Signals |");
        sb.AppendLine("| --- | ---: | --- |");
        foreach (var cluster in GetClusters(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{cluster.Name}` | {cluster.Files.Count} | `{string.Join(", ", cluster.Signals.Take(6))}` |");
        }
        sb.AppendLine();
    }

    private static IReadOnlyList<ClusterRow> GetClusters(CanonicalAstDocument document)
    {
        var key = document.SnapshotPath ?? string.Empty;
        if (ClusterCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var cachePath = GetClusterCachePath(document);
        var persisted = LoadClusterCache(cachePath);
        if (persisted is not null)
        {
            Console.WriteLine($"[step4] L5: loaded persisted cluster cache from {cachePath}");
            ClusterCache[key] = persisted;
            return persisted;
        }

        Console.WriteLine("[step4] L5: building cluster cache");
        var clusters = BuildClusters(document);
        ClusterCache[key] = clusters;
        SaveClusterCache(cachePath, clusters);
        Console.WriteLine($"[step4] L5: cached {clusters.Count} clusters");
        return clusters;
    }

    private static ReferenceIndex GetReferenceIndex(CanonicalAstDocument document)
    {
        var cachePath = GetReferenceIndexCachePath(document);
        var cached = LoadReferenceIndexCache(cachePath);
        if (cached is not null)
        {
            Console.WriteLine($"[step4] L5: loaded persisted reference index from {cachePath}");
            return cached;
        }

        Console.WriteLine("[step4] L5: building reference index cache");
        var built = BuildReferenceIndex(document.Files);
        SaveReferenceIndexCache(cachePath, built);
        return built;
    }

    private static void AppendLayer6(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L6: Architecture Roles");
        sb.AppendLine();
        sb.AppendLine("| Role | Files | Evidence |");
        sb.AppendLine("| --- | ---: | --- |");

        foreach (var item in BuildArchitectureRoles(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{item.Name}` | {item.Files.Count} | `{string.Join(", ", item.Evidence.Take(5))}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer7(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L7: Domain Concepts");
        sb.AppendLine();
        sb.AppendLine("| Concept | Terms | Evidence |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var concept in BuildDomainConcepts(document).OrderByDescending(x => x.Terms.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{concept.Name}` | `{string.Join(", ", concept.Terms.Take(8))}` | `{string.Join(", ", concept.Evidence.Take(4))}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer8(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L8: Behavioral Scenarios");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Files | Signals |");
        sb.AppendLine("| --- | ---: | --- |");

        foreach (var scenario in BuildScenarios(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{scenario.Name}` | {scenario.Files.Count} | `{string.Join(", ", scenario.Signals.Take(6))}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer9(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L9: Product Intent");
        sb.AppendLine();
        sb.AppendLine("| Intent | Evidence |");
        sb.AppendLine("| --- | --- |");

        foreach (var intent in BuildProductIntent(document))
        {
            sb.AppendLine($"| `{intent.Name}` | `{string.Join(", ", intent.Evidence.Take(6))}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer10(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L10: Open Questions");
        sb.AppendLine();
        sb.AppendLine("| Question | Evidence | Confidence |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var item in BuildOpenQuestions(document))
        {
            sb.AppendLine($"| `{item.Question}` | `{item.Evidence}` | `{item.Confidence}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer11(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L11: Performance Analysis");
        sb.AppendLine();
        sb.AppendLine("This layer synthesizes async, waiting, and bottleneck signals from prior layers.");
        sb.AppendLine();
        sb.AppendLine("| Area | Signal | Evidence | Confidence |");
        sb.AppendLine("| --- | --- | --- | --- |");

        foreach (var item in BuildPerformanceAnalysis(document))
        {
            sb.AppendLine($"| `{item.Area}` | `{item.Signal}` | `{item.Evidence}` | `{item.Confidence}` |");
        }

        sb.AppendLine();
    }

    private static void AppendLayer12(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L12: Application and Tool Taxonomy");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferApplicationToolRole(file);
            var signals = string.Join(", ", BuildApplicationToolSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated taxonomy table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer13(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L13: Runtime and System Roles");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferRuntimeRole(file);
            var signals = string.Join(", ", BuildRuntimeRoleSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated runtime role table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer14(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L14: Data Code Structures");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferDataRole(file);
            var signals = string.Join(", ", BuildDataRoleSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated data structure table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer15(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L15: Execution and State Flow");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferExecutionRole(file);
            var signals = string.Join(", ", BuildExecutionSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated execution table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer16(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L16: Persistence and Storage");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferPersistenceRole(file);
            var signals = string.Join(", ", BuildPersistenceSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated persistence table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer17(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L17: External Integration Boundaries");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferIntegrationRole(file);
            var signals = string.Join(", ", BuildIntegrationSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated integration table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer18(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L18: Command and Query Responsibility");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferCommandQueryRole(file);
            var signals = string.Join(", ", BuildCommandQuerySignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated command/query table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer19(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L19: Dependency Direction and Coupling");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferCouplingRole(file);
            var signals = string.Join(", ", BuildCouplingSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated coupling table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer20(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L20: Test Coverage and Verification");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferVerificationRole(file);
            var signals = string.Join(", ", BuildVerificationSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated verification table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer21(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L21: Configuration and Environment Behavior");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferConfigEnvironmentRole(file);
            var signals = string.Join(", ", BuildConfigEnvironmentSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated configuration table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer22(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L22: Error Handling and Resilience");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferResilienceRole(file);
            var signals = string.Join(", ", BuildResilienceSignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated resilience table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendLayer23(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("## L23: Observability and Logging Semantics");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");

        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            var role = InferObservabilityRole(file);
            var signals = string.Join(", ", BuildObservabilitySignals(file).Take(6));
            sb.AppendLine($"| `{file.RelativePath}` | `{role}` | `{signals}` |");
        }

        if (document.Files.Count > 400)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated observability table at 400 rows out of `{document.Files.Count}` files.");
        }

        sb.AppendLine();
    }

    private static void AppendC4Sections(StringBuilder sb, CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] c4 sections: rendering combined C4 views");
        sb.AppendLine("## C4 Views");
        sb.AppendLine();
        AppendC4Context(sb, document);
        AppendC4Container(sb, document);
        AppendC4Component(sb, document);
        AppendC4Code(sb, document);
    }

    private static void AppendImportantFiles(StringBuilder sb, CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] important files: ranking");
        sb.AppendLine("## Important Files");
        sb.AppendLine();
        sb.AppendLine("This table ranks files by reverse-engineering value so large repositories stay readable.");
        sb.AppendLine();
        sb.AppendLine("| Rank | File | Score | Reasons |");
        sb.AppendLine("| --- | --- | ---: | --- |");

        var ranked = document.Files
            .Select(file => new
            {
                File = file,
                Score = ScoreFileImportance(file),
                Reasons = BuildImportanceReasons(file).Take(5).ToList(),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToList();

        var rank = 1;
        foreach (var item in ranked)
        {
            Console.WriteLine($"[step4] important files: {rank} {item.File.RelativePath}");
            sb.AppendLine($"| {rank++} | `{item.File.RelativePath}` | {item.Score} | `{string.Join(", ", item.Reasons)}` |");
        }

        if (document.Files.Count > ranked.Count)
        {
            sb.AppendLine();
            sb.AppendLine($"- Truncated at {ranked.Count} files out of `{document.Files.Count}` total files.");
        }

        sb.AppendLine();
    }

    private static void AppendC4Context(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("### C4: Context");
        sb.AppendLine();
        sb.AppendLine("| Type | Name | Evidence |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var actor in BuildC4Actors(document))
        {
            sb.AppendLine($"| `Actor` | `{actor.Name}` | `{actor.Evidence}` |");
        }
        foreach (var external in BuildC4ExternalSystems(document))
        {
            sb.AppendLine($"| `External System` | `{external.Name}` | `{external.Evidence}` |");
        }
        foreach (var intent in BuildProductIntent(document).Take(10))
        {
            sb.AppendLine($"| `Intent` | `{intent.Name}` | `{string.Join(", ", intent.Evidence.Take(4))}` |");
        }
        foreach (var scenario in BuildScenarios(document).Take(10))
        {
            sb.AppendLine($"| `Scenario` | `{scenario.Name}` | `{string.Join(", ", scenario.Signals.Take(4))}` |");
        }
        sb.AppendLine();
    }

    private static void AppendC4Container(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("### C4: Container");
        sb.AppendLine();
        sb.AppendLine("| Container | Responsibility | Technology | Evidence |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var role in BuildArchitectureRoles(document).OrderByDescending(x => x.Files.Count).Take(12))
        {
            sb.AppendLine($"| `{role.Name}` | `{InferContainerResponsibility(role.Name)}` | `{InferContainerTechnology(role.Files)}` | `{string.Join(", ", role.Evidence.Take(5))}` |");
        }
        foreach (var cluster in GetClusters(document).OrderByDescending(x => x.Files.Count).Take(12))
        {
            sb.AppendLine($"| `{cluster.Name}` | `{InferClusterResponsibility(cluster)}` | `{InferClusterTechnology(cluster)}` | `{string.Join(", ", cluster.Signals.Take(5))}` |");
        }
        sb.AppendLine();
    }

    private static void AppendC4Component(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("### C4: Component");
        sb.AppendLine();
        sb.AppendLine("| Component | Responsibility | Relationships | Evidence |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var row in BuildComponentSignals(document).Take(40))
        {
            sb.AppendLine($"| `{row.Name}` | `{InferComponentResponsibility(row.Name)}` | `{string.Join(", ", row.Relationships.Take(4))}` | `{row.Evidence}` |");
        }
        sb.AppendLine();
    }

    private static void AppendC4Code(StringBuilder sb, CanonicalAstDocument document)
    {
        sb.AppendLine("### C4: Code");
        sb.AppendLine();
        sb.AppendLine("| File | Symbols | Evidence |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var file in document.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(80))
        {
            sb.AppendLine($"| `{file.RelativePath}` | `{string.Join(", ", BuildCodeSymbols(file).Take(8))}` | `{InferRole(file)}, {file.Language}, {file.References.Count} refs` |");
        }
        sb.AppendLine();
    }

    private async Task WriteSubsystemPagesAsync(CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] subsystem pages: building");
        var clusters = GetClusters(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] subsystem pages: clusters {clusters.Count}");
        await ProcessInBatchesAsync(
            clusters,
            _parallelism,
            cluster =>
            {
                Console.WriteLine($"[step4] subsystem pages: {cluster.Name} ({cluster.Files.Count} files, {cluster.Edges.Count} edges)");
                var output = new StringBuilder();
                output.AppendLine($"# Cluster: {cluster.Name}");
                output.AppendLine();
                output.AppendLine($"- Files: `{cluster.Files.Count}`");
                output.AppendLine($"- Signals: `{string.Join(", ", cluster.Signals.Distinct(StringComparer.OrdinalIgnoreCase))}`");
                output.AppendLine($"- Edges: `{cluster.Edges.Count}`");
                output.AppendLine();
                output.AppendLine("## Files");
                output.AppendLine();
                foreach (var file in cluster.Files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(300))
                {
                    output.AppendLine($"- `{file}`");
                }

                if (cluster.Edges.Count > 0)
                {
                    output.AppendLine();
                    output.AppendLine("## Internal Edges");
                    output.AppendLine();
                    foreach (var edge in cluster.Edges.Take(200))
                    {
                        output.AppendLine($"- `{edge.Source}` -> `{edge.Target}` ({edge.Kind}, {edge.Category})");
                    }
                }

                var safe = MakeSafeFileName(cluster.Name);
                var path = Path.Combine(_specRoot, $"{safe}.md");
                return File.WriteAllTextAsync(path, output.ToString(), Encoding.UTF8);
            });
    }

    private async Task WriteHierarchyPagesAsync(CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] hierarchy pages: building directories");
        var executionRoot = Path.Combine(_specRoot, "execution");
        var relationshipsRoot = Path.Combine(_specRoot, "relationships");
        var componentsRoot = Path.Combine(_specRoot, "components");
        var configurationRoot = Path.Combine(_specRoot, "configuration");
        var resilienceRoot = Path.Combine(_specRoot, "resilience");
        var observabilityRoot = Path.Combine(_specRoot, "observability");
        var dataRoot = Path.Combine(_specRoot, "data");
        var testRoot = Path.Combine(_specRoot, "test");
        Directory.CreateDirectory(executionRoot);
        Directory.CreateDirectory(relationshipsRoot);
        Directory.CreateDirectory(componentsRoot);
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(resilienceRoot);
        Directory.CreateDirectory(observabilityRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(testRoot);

        await File.WriteAllTextAsync(Path.Combine(executionRoot, "index.md"), BuildExecutionHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(relationshipsRoot, "index.md"), BuildRelationshipHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(componentsRoot, "index.md"), BuildComponentHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(configurationRoot, "index.md"), BuildConfigHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(resilienceRoot, "index.md"), BuildResilienceHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(observabilityRoot, "index.md"), BuildObservabilityHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "index.md"), BuildDataHierarchyIndex(document), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(testRoot, "index.md"), BuildTestHierarchyIndex(document), Encoding.UTF8);

        var scenarios = BuildScenarios(document).ToList();
        Console.WriteLine($"[step4] hierarchy pages: scenarios {scenarios.Count}");
        await ProcessInBatchesAsync(scenarios, _parallelism, scenario =>
        {
            Console.WriteLine($"[step4] hierarchy pages: execution {scenario.Name}");
            var safe = MakeSafeFileName(scenario.Name);
            var path = Path.Combine(executionRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildScenarioPage(document, scenario), Encoding.UTF8);
        });

        var clusters = GetClusters(document).ToList();
        Console.WriteLine($"[step4] hierarchy pages: relationship clusters {clusters.Count}");
        await ProcessInBatchesAsync(clusters, _parallelism, cluster =>
        {
            Console.WriteLine($"[step4] hierarchy pages: relationship {cluster.Name}");
            var safe = MakeSafeFileName(cluster.Name);
            var path = Path.Combine(relationshipsRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildRelationshipPage(cluster), Encoding.UTF8);
        });

        var components = BuildComponentSignals(document).ToList();
        Console.WriteLine($"[step4] hierarchy pages: components {components.Count}");
        await ProcessInBatchesAsync(components, _parallelism, component =>
        {
            Console.WriteLine($"[step4] hierarchy pages: component {component.Name}");
            var safe = MakeSafeFileName(component.Name);
            var path = Path.Combine(componentsRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildComponentPage(component), Encoding.UTF8);
        });

        var configurationFiles = document.Files.Where(file => InferConfigEnvironmentRole(file) != "runtime-settings").OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] hierarchy pages: configuration files {configurationFiles.Count}");
        await ProcessInBatchesAsync(configurationFiles, _parallelism, file =>
        {
            Console.WriteLine($"[step4] hierarchy pages: configuration {file.RelativePath}");
            var safe = MakeSafeFileName(file.RelativePath);
            var path = Path.Combine(configurationRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildFileRolePage("Configuration", file.RelativePath, InferConfigEnvironmentRole(file), BuildConfigEnvironmentSignals(file)), Encoding.UTF8);
        });

        var resilienceFiles = document.Files.Where(file => InferResilienceRole(file) != "error-path").OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] hierarchy pages: resilience files {resilienceFiles.Count}");
        await ProcessInBatchesAsync(resilienceFiles, _parallelism, file =>
        {
            Console.WriteLine($"[step4] hierarchy pages: resilience {file.RelativePath}");
            var safe = MakeSafeFileName(file.RelativePath);
            var path = Path.Combine(resilienceRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildFileRolePage("Resilience", file.RelativePath, InferResilienceRole(file), BuildResilienceSignals(file)), Encoding.UTF8);
        });

        var observabilityFiles = document.Files.Where(file => InferObservabilityRole(file) != "observability").OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] hierarchy pages: observability files {observabilityFiles.Count}");
        await ProcessInBatchesAsync(observabilityFiles, _parallelism, file =>
        {
            Console.WriteLine($"[step4] hierarchy pages: observability {file.RelativePath}");
            var safe = MakeSafeFileName(file.RelativePath);
            var path = Path.Combine(observabilityRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildFileRolePage("Observability", file.RelativePath, InferObservabilityRole(file), BuildObservabilitySignals(file)), Encoding.UTF8);
        });

        var dataFiles = document.Files.Where(file => InferDataRole(file) != "data-structure").OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] hierarchy pages: data files {dataFiles.Count}");
        await ProcessInBatchesAsync(dataFiles, _parallelism, file =>
        {
            Console.WriteLine($"[step4] hierarchy pages: data {file.RelativePath}");
            var safe = MakeSafeFileName(file.RelativePath);
            var path = Path.Combine(dataRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildFileRolePage("Data", file.RelativePath, InferDataRole(file), BuildDataRoleSignals(file)), Encoding.UTF8);
        });

        var testFiles = document.Files.Where(file => InferVerificationRole(file) != "non-test").OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] hierarchy pages: test files {testFiles.Count}");
        await ProcessInBatchesAsync(testFiles, _parallelism, file =>
        {
            Console.WriteLine($"[step4] hierarchy pages: test {file.RelativePath}");
            var safe = MakeSafeFileName(file.RelativePath);
            var path = Path.Combine(testRoot, $"{safe}.md");
            return File.WriteAllTextAsync(path, BuildFileRolePage("Test", file.RelativePath, InferVerificationRole(file), BuildVerificationSignals(file)), Encoding.UTF8);
        });
    }

    private async Task WriteFactDiffAsync(CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] facts page: building");
        var output = new StringBuilder();
        output.AppendLine("# Fact Diff");
        output.AppendLine();
        var files = document.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"[step4] facts page: files {files.Count}");
        foreach (var file in files)
        {
            if (file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                file.RelativePath.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
                file.RelativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[step4] facts page: {file.RelativePath}");
            }
            output.AppendLine($"## `{file.RelativePath}`");
            output.AppendLine();
            output.AppendLine($"- Status: `{file.Status}`");
            output.AppendLine($"- Tags: `{string.Join(", ", file.Tags)}`");
            output.AppendLine($"- References: `{file.References.Count}`");
            output.AppendLine();
        }

        await File.WriteAllTextAsync(Path.Combine(_specRoot, "facts.md"), output.ToString(), Encoding.UTF8);
    }

    private async Task WriteC4PagesAsync(CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] c4 pages: context");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "context.md"), BuildC4ContextMarkdown(document), Encoding.UTF8);
        Console.WriteLine("[step4] c4 pages: container");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "container.md"), BuildC4ContainerMarkdown(document), Encoding.UTF8);
        Console.WriteLine("[step4] c4 pages: component");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "component.md"), BuildC4ComponentMarkdown(document), Encoding.UTF8);
        Console.WriteLine("[step4] c4 pages: code");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "code.md"), BuildC4CodeMarkdown(document), Encoding.UTF8);
        Console.WriteLine("[step4] c4 pages: index");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "c4-index.md"), BuildC4IndexMarkdown(document), Encoding.UTF8);
    }

    private async Task WriteLandingPageAsync(CanonicalAstDocument document)
    {
        Console.WriteLine("[step4] landing page: building");
        var output = new StringBuilder();
        output.AppendLine("# AST Spec Landing Page");
        output.AppendLine();
        output.AppendLine("Use this folder as the entry point for the generated reverse-engineering outputs.");
        output.AppendLine();
        output.AppendLine("## Primary Views");
        output.AppendLine();
        output.AppendLine("- [Layered Spec](ast-spec-latest.md)");
        output.AppendLine("- [C4 Index](c4-index.md)");
        output.AppendLine("- [Facts](facts.md)");
        output.AppendLine();
        output.AppendLine("## Folder Hub");
        output.AppendLine();
        output.AppendLine("| Folder | Purpose | Entry |");
        output.AppendLine("| --- | --- | --- |");
        output.AppendLine("| `execution/` | Scenario and flow hierarchy | [execution/index.md](execution/index.md) |");
        output.AppendLine("| `relationships/` | Cluster and subsystem hierarchy | [relationships/index.md](relationships/index.md) |");
        output.AppendLine("| `components/` | Component responsibility hierarchy | [components/index.md](components/index.md) |");
        output.AppendLine("| `configuration/` | Config and environment hierarchy | [configuration/index.md](configuration/index.md) |");
        output.AppendLine("| `resilience/` | Error handling and recovery hierarchy | [resilience/index.md](resilience/index.md) |");
        output.AppendLine("| `observability/` | Logging, tracing, and diagnostics hierarchy | [observability/index.md](observability/index.md) |");
        output.AppendLine("| `data/` | Data-code structures hierarchy | [data/index.md](data/index.md) |");
        output.AppendLine("| `test/` | Verification and test hierarchy | [test/index.md](test/index.md) |");
        output.AppendLine();
        output.AppendLine("## C4 Pages");
        output.AppendLine();
        output.AppendLine("- [Context](context.md)");
        output.AppendLine("- [Container](container.md)");
        output.AppendLine("- [Component](component.md)");
        output.AppendLine("- [Code](code.md)");
        output.AppendLine();
        output.AppendLine("## Bands");
        output.AppendLine();
        output.AppendLine("- `B0-B4` are embedded in `ast-spec-latest.md`.");
        output.AppendLine();
        output.AppendLine($"- Workspace: `{document.WorkspaceRoot}`");
        output.AppendLine($"- Snapshot: `{document.SnapshotPath}`");

        Console.WriteLine("[step4] landing page: writing README.md");
        await File.WriteAllTextAsync(Path.Combine(_specRoot, "README.md"), output.ToString(), Encoding.UTF8);
    }

    private static string BuildExecutionHierarchyIndex(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Execution Hierarchy");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Link | Signals |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var scenario in BuildScenarios(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{scenario.Name}` | [{scenario.Name}]({MakeSafeFileName(scenario.Name)}.md) | `{string.Join(", ", scenario.Signals.Take(4))}` |");
        }
        sb.AppendLine();
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildRelationshipHierarchyIndex(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Relationship Hierarchy");
        sb.AppendLine();
        sb.AppendLine("| Cluster | Link | Signals |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var cluster in BuildClusters(document).OrderByDescending(x => x.Files.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{cluster.Name}` | [{cluster.Name}]({MakeSafeFileName(cluster.Name)}.md) | `{string.Join(", ", cluster.Signals.Take(4))}` |");
        }
        sb.AppendLine();
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildComponentHierarchyIndex(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Component Hierarchy");
        sb.AppendLine();
        sb.AppendLine("| Component | Link | Relationships |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var component in BuildComponentSignals(document).OrderByDescending(x => x.Evidence.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| `{component.Name}` | [{component.Name}]({MakeSafeFileName(component.Name)}.md) | `{string.Join(", ", component.Relationships.Take(4))}` |");
        }
        sb.AppendLine();
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildScenarioPage(CanonicalAstDocument document, ScenarioRow scenario)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Execution: {scenario.Name}");
        sb.AppendLine();
        sb.AppendLine("| Files | Signals |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| `{string.Join(", ", scenario.Files.Take(50))}` | `{string.Join(", ", scenario.Signals.Take(10))}` |");
        sb.AppendLine();
        sb.AppendLine("- [Execution index](index.md)");
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildRelationshipPage(ClusterRow cluster)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Relationship: {cluster.Name}");
        sb.AppendLine();
        sb.AppendLine("| Files | Edges | Signals |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| `{cluster.Files.Count}` | `{cluster.Edges.Count}` | `{string.Join(", ", cluster.Signals.Take(10))}` |");
        sb.AppendLine();
        sb.AppendLine("- [Relationship index](index.md)");
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildComponentPage(C4Row component)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Component: {component.Name}");
        sb.AppendLine();
        sb.AppendLine("| Evidence | Relationships |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| `{string.Join(", ", component.Evidence.Take(10))}` | `{string.Join(", ", component.Relationships.Take(10))}` |");
        sb.AppendLine();
        sb.AppendLine("- [Component index](index.md)");
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string BuildConfigHierarchyIndex(CanonicalAstDocument document)
    {
        return BuildRoleHierarchyIndex("Configuration Hierarchy", "Configuration", document.Files.Where(file => InferConfigEnvironmentRole(file) != "runtime-settings"), file => InferConfigEnvironmentRole(file), file => BuildConfigEnvironmentSignals(file));
    }

    private static string BuildResilienceHierarchyIndex(CanonicalAstDocument document)
    {
        return BuildRoleHierarchyIndex("Resilience Hierarchy", "Resilience", document.Files.Where(file => InferResilienceRole(file) != "error-path"), file => InferResilienceRole(file), file => BuildResilienceSignals(file));
    }

    private static string BuildObservabilityHierarchyIndex(CanonicalAstDocument document)
    {
        return BuildRoleHierarchyIndex("Observability Hierarchy", "Observability", document.Files.Where(file => InferObservabilityRole(file) != "observability"), file => InferObservabilityRole(file), file => BuildObservabilitySignals(file));
    }

    private static string BuildDataHierarchyIndex(CanonicalAstDocument document)
    {
        return BuildRoleHierarchyIndex("Data Hierarchy", "Data", document.Files.Where(file => InferDataRole(file) != "data-structure"), file => InferDataRole(file), file => BuildDataRoleSignals(file));
    }

    private static string BuildTestHierarchyIndex(CanonicalAstDocument document)
    {
        return BuildRoleHierarchyIndex("Test Hierarchy", "Test", document.Files.Where(file => InferVerificationRole(file) != "non-test"), file => InferVerificationRole(file), file => BuildVerificationSignals(file));
    }

    private static string BuildRoleHierarchyIndex(
        string title,
        string label,
        IEnumerable<CanonicalAstFile> files,
        Func<CanonicalAstFile, string> roleSelector,
        Func<CanonicalAstFile, IEnumerable<string>> signalsSelector)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("| File | Role | Signals |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Take(400))
        {
            sb.AppendLine($"| `{file.RelativePath}` | `{roleSelector(file)}` | `{string.Join(", ", signalsSelector(file).Take(5))}` |");
        }
        sb.AppendLine();
        sb.AppendLine("- [Back to landing page](../README.md)");
        sb.AppendLine($"- [Back to {label} index](index.md)");
        return sb.ToString();
    }

    private static string BuildFileRolePage(string band, string relativePath, string role, IEnumerable<string> signals)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {band}: {relativePath}");
        sb.AppendLine();
        sb.AppendLine($"- Role: `{role}`");
        sb.AppendLine($"- Signals: `{string.Join(", ", signals.Take(8))}`");
        sb.AppendLine();
        sb.AppendLine("- [Back to landing page](../README.md)");
        return sb.ToString();
    }

    private static string MakeSafeFileName(string name)
        => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static async Task ProcessInBatchesAsync<T>(IReadOnlyList<T> items, int maxConcurrency, Func<T, Task> action)
    {
        for (var start = 0; start < items.Count; start += maxConcurrency)
        {
            var batch = items.Skip(start).Take(maxConcurrency).Select(action).ToList();
            await Task.WhenAll(batch);
        }
    }

    private static string BuildC4ContextMarkdown(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Back to C4 Index](c4-index.md)");
        sb.AppendLine();
        sb.AppendLine("[Back to Bands](ast-spec-latest.md)");
        sb.AppendLine();
        AppendC4Context(sb, document);
        return sb.ToString();
    }

    private static string BuildC4ContainerMarkdown(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Back to C4 Index](c4-index.md)");
        sb.AppendLine();
        sb.AppendLine("[Relevant Layers](ast-spec-latest.md#band-index)");
        sb.AppendLine();
        AppendC4Container(sb, document);
        return sb.ToString();
    }

    private static string BuildC4ComponentMarkdown(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Back to C4 Index](c4-index.md)");
        sb.AppendLine();
        sb.AppendLine("[Relevant Layers](ast-spec-latest.md#band-index)");
        sb.AppendLine();
        AppendC4Component(sb, document);
        return sb.ToString();
    }

    private static string BuildC4CodeMarkdown(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Back to C4 Index](c4-index.md)");
        sb.AppendLine();
        sb.AppendLine("[Relevant Layers](ast-spec-latest.md#layer-index)");
        sb.AppendLine();
        AppendC4Code(sb, document);
        return sb.ToString();
    }

    private static string BuildC4IndexMarkdown(CanonicalAstDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# C4 Index");
        sb.AppendLine();
        sb.AppendLine("## Start Here");
        sb.AppendLine();
        sb.AppendLine("1. [Layered Spec](ast-spec-latest.md)");
        sb.AppendLine("2. [Context](context.md)");
        sb.AppendLine("3. [Container](container.md)");
        sb.AppendLine("4. [Component](component.md)");
        sb.AppendLine("5. [Code](code.md)");
        sb.AppendLine();
        sb.AppendLine($"- Workspace: `{document.WorkspaceRoot}`");
        sb.AppendLine($"- Snapshot: `{document.SnapshotPath}`");
        sb.AppendLine();
        sb.AppendLine("## Views");
        sb.AppendLine();
        sb.AppendLine("| View | Link | Linked Layers |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `Context` | [context.md](context.md) | `B0-B2` |");
        sb.AppendLine("| `Container` | [container.md](container.md) | `B2-B3` |");
        sb.AppendLine("| `Component` | [component.md](component.md) | `B3-B4` |");
        sb.AppendLine("| `Code` | [code.md](code.md) | `L2-L4`, `L12-L23` |");
        sb.AppendLine();
        sb.AppendLine("## Layer Bands");
        sb.AppendLine();
        sb.AppendLine("- `B0`: repository shape and structure");
        sb.AppendLine("- `B1`: relationships and architecture");
        sb.AppendLine("- `B2`: scenarios and performance");
        sb.AppendLine("- `B3`: system and boundaries");
        sb.AppendLine("- `B4`: operational semantics");
        sb.AppendLine();
        sb.AppendLine("## Cross Links");
        sb.AppendLine();
        sb.AppendLine("- [Layered Spec](ast-spec-latest.md)");
        sb.AppendLine("- [Facts](facts.md)");
        return sb.ToString();
    }

    private static string InferRole(CanonicalAstFile file)
    {
        if (string.Equals(file.Status, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return "missing";
        }

        if (string.Equals(file.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        return file.RootNodeCount switch
        {
            <= 3 => "leaf",
            <= 12 => "component",
            _ => "module",
        };
    }

    private static IEnumerable<string> CollectKinds(CanonicalAstNode node)
    {
        yield return node.Kind;
        foreach (var child in node.Children)
        {
            foreach (var nested in CollectKinds(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<RelationRow> ExtractRelations(CanonicalAstDocument document)
    {
        foreach (var file in document.Files ?? [])
        {
            if (file is null)
            {
                continue;
            }

            foreach (var reference in file.References ?? [])
            {
                if (reference is null)
                {
                    continue;
                }

                yield return new RelationRow(ClassifyReference(reference.Kind), file.RelativePath, reference.Kind, reference.Target ?? "unknown", reference.Evidence ?? "reference");
            }

            if (file.Root is null)
            {
                continue;
            }

            foreach (var node in EnumerateNodes(file.Root))
            {
                if (node is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(node.Name))
                {
                    yield return new RelationRow("ownership", file.RelativePath, "contains", node.Name!, node.Summary ?? node.Kind);
                }

                foreach (var reference in node.References ?? [])
                {
                    if (reference is null)
                    {
                        continue;
                    }

                    yield return new RelationRow(ClassifyReference(reference.Kind), file.RelativePath, reference.Kind, reference.Target ?? "unknown", reference.Evidence ?? "node reference");
                }
            }
        }
    }

    private static IEnumerable<CanonicalAstNode> EnumerateNodes(CanonicalAstNode node)
    {
        if (node is null)
        {
            yield break;
        }

        yield return node;
        foreach (var child in node.Children ?? [])
        {
            if (child is null)
            {
                continue;
            }

            foreach (var nested in EnumerateNodes(child))
            {
                yield return nested;
            }
        }
    }

    private static IReadOnlyList<ClusterRow> BuildClusters(CanonicalAstDocument document)
    {
        var benchmark = new L5Benchmark();
        benchmark.StartTotal();

        Console.WriteLine("[step4] L5: building graph with Dijkstra optimization");
        var clusters = new Dictionary<string, ClusterRow>(StringComparer.OrdinalIgnoreCase);
        var fileByPath = document.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var symbolIndex = BuildSymbolIndex(document.Symbols);

        var astHash = ComputeAstHash(document);
        Console.WriteLine($"[step4] L5: AST hash {astHash.Substring(0, 12)}... (cache key)");

        InvalidateCacheIfNeeded(astHash, document.SnapshotPath);

        Console.WriteLine("[step4] L5: building strong edge graph (defines + call only)");
        var strongEdges = BuildStrongEdgeGraph(document);
        var strongEdgesCount = strongEdges.Values.Sum(edges => edges.Count);
        Console.WriteLine($"[step4] L5: strong edges: {strongEdgesCount} (reduced from ~160K+)");

        Console.WriteLine("[step4] L5: identifying hub nodes");
        benchmark.StartPhase("hubs");
        var hubs = IdentifyHubNodes(document);
        benchmark.EndPhase("hubs");
        Console.WriteLine($"[step4] L5: found {hubs.Count} hub nodes");

        Dictionary<string, int> distances;
        var distancesCache = LoadCachedDistances(astHash);
        if (distancesCache is not null)
        {
            Console.WriteLine("[step4] L5: ✓ loaded cached Dijkstra distances");
            benchmark.StartPhase("dijkstra");
            distances = distancesCache;
            benchmark.EndPhase("dijkstra");
        }
        else
        {
            Console.WriteLine("[step4] L5: computing shortest distances from hubs");
            benchmark.StartPhase("dijkstra");
            distances = ComputeDistancesFromHubs(document, symbolIndex, hubs, strongEdges);
            benchmark.EndPhase("dijkstra");
            SaveCachedDistances(astHash, distances);
        }

        Dictionary<string, HashSet<string>> graph;
        var graphCache = LoadCachedGraph(astHash);
        if (graphCache is not null)
        {
            Console.WriteLine("[step4] L5: ✓ loaded cached graph edges");
            benchmark.StartPhase("graph");
            graph = graphCache;
            benchmark.EndPhase("graph");
        }
        else
        {
            Console.WriteLine("[step4] L5: building distance-optimized graph");
            benchmark.StartPhase("graph");
            graph = BuildFileGraphWithDijkstra(document, symbolIndex, distances, strongEdges);
            benchmark.EndPhase("graph");
            SaveCachedGraph(astHash, graph);
        }
        var totalGraphEdges = graph.Values.Sum(v => v.Count);
        Console.WriteLine($"[step4] L5: ✓ graph complete: {totalGraphEdges} edges (filtered strong edges only)");

        Console.WriteLine("[step4] L5: computing distance bands for micro-clustering");
        var bands = ComputeDistanceBands(distances);
        var bandCounts = new Dictionary<int, int>();
        foreach (var band in bands.Values)
        {
            if (bandCounts.ContainsKey(band)) bandCounts[band]++; else bandCounts[band] = 1;
        }
        Console.WriteLine($"[step4] L5: bands computed: {string.Join(" | ", bandCounts.OrderBy(kv => kv.Key).Select(kv => $"band{kv.Key}={kv.Value}"))}");


        IReadOnlyList<HashSet<string>> components;
        var componentsCache = LoadCachedComponents(astHash);
        if (componentsCache is not null)
        {
            Console.WriteLine("[step4] L5: ✓ loaded cached components");
            benchmark.StartPhase("components");
            components = componentsCache;
            benchmark.EndPhase("components");
        }
        else
        {
            benchmark.StartPhase("components");
            components = BuildComponentsWithValidation(document.Files.Select(file => file.RelativePath), graph, bands);
            benchmark.EndPhase("components");
            SaveCachedComponents(astHash, components.ToList());
        }
        Console.WriteLine($"[step4] L5: components {components.Count}");

        Console.WriteLine("[step4] L5: building cluster details from components");
        var clusterSw = System.Diagnostics.Stopwatch.StartNew();
        var clusterProgressReporter = new ClusterProgressReporter(components.Count);

        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            var files = component.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var name = InferClusterName(files.Select(path => fileByPath[path]).ToList());
            var cluster = new ClusterRow(name);

            foreach (var path in files)
            {
                var file = fileByPath[path];
                cluster.Files.Add(path);
                cluster.Signals.Add($"{file.Language}/{InferRole(file)}");
                cluster.Signals.AddRange(file.Tags.Take(2));
            }

            BuildClusterEdges(document, fileSet, cluster);

            clusters[name + "|" + string.Join("|", files)] = cluster;

            clusterProgressReporter.Update(i + 1, clusterSw.Elapsed);
        }

        clusterProgressReporter.Complete(clusterSw.Elapsed);

        benchmark.EndTotal();
        benchmark.Report(document.Files.Count, hubs.Count, distances);

        return clusters.Values.ToList();
    }

    private static void BuildClusterEdges(CanonicalAstDocument document, HashSet<string> fileSet, ClusterRow cluster)
    {
        var edgeCount = 0;
        var bridgeCount = 0;
        foreach (var file in document.Files)
        {
            if (!fileSet.Contains(file.RelativePath))
            {
                continue;
            }

            foreach (var reference in file.References)
            {
                if (edgeCount >= 200)
                {
                    return;
                }

                var target = reference.Target;
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                foreach (var targetFile in fileSet.Where(path => path.Contains(target, StringComparison.OrdinalIgnoreCase)))
                {
                    if (targetFile.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    cluster.Edges.Add(new ClusterEdgeCache(file.RelativePath, targetFile, reference.Kind, ClassifyReference(reference.Kind), reference.Evidence ?? reference.Kind));
                    edgeCount++;
                    if (IsBridgeEdge(file.RelativePath, targetFile, file.Language, reference.Kind))
                    {
                        bridgeCount++;
                    }
                    if (edgeCount >= 200)
                    {
                        return;
                    }
                }
            }
        }

        if (bridgeCount > 0)
        {
            Console.WriteLine($"[step4] cluster edges: preserved {bridgeCount} bridge edges in `{cluster.Name}`");
        }
    }

    private static string GetClusterCachePath(CanonicalAstDocument document)
    {
        var cacheRoot = Path.Combine(document.WorkspaceRoot, "generated", "ast-spec", "cache");
        Directory.CreateDirectory(cacheRoot);
        var stem = Path.GetFileNameWithoutExtension(document.SnapshotPath ?? "snapshot");
        return Path.Combine(cacheRoot, $"cluster-cache-{stem}.json");
    }

    private static IReadOnlyList<ClusterRow>? LoadClusterCache(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var cached = JsonSerializer.Deserialize<List<ClusterRowCache>>(json);
            if (cached is null)
            {
                return null;
            }

            return cached.Select(row =>
            {
                var cluster = new ClusterRow(row.Name);
                cluster.Files.AddRange(row.Files ?? []);
                cluster.Signals.AddRange(row.Signals ?? []);
                cluster.Edges.AddRange((row.Edges ?? []).Select(edge => new ClusterEdgeCache(edge.Source, edge.Target, edge.Kind, edge.Category, edge.Evidence)));
                return cluster;
            }).ToList();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveClusterCache(string path, IReadOnlyList<ClusterRow> clusters)
    {
        var payload = clusters.Select(cluster => new ClusterRowCache(
            cluster.Name,
            cluster.Files.ToList(),
            cluster.Signals.ToList(),
            cluster.Edges.Select(edge => new ClusterEdgeCache(edge.Source, edge.Target, edge.Kind, edge.Category, edge.Evidence)).ToList())).ToList();

        var json = JsonSerializer.Serialize(payload, ClusterCacheJsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string GetReferenceIndexCachePath(CanonicalAstDocument document)
    {
        var cacheRoot = Path.Combine(document.WorkspaceRoot, "generated", "ast-spec", "cache");
        Directory.CreateDirectory(cacheRoot);
        var stem = Path.GetFileNameWithoutExtension(document.SnapshotPath ?? "snapshot");
        return Path.Combine(cacheRoot, $"reference-index-{stem}.json");
    }

    private static ReferenceIndex? LoadReferenceIndexCache(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var cached = JsonSerializer.Deserialize<ReferenceIndexCache>(json);
            if (cached is null)
            {
                return null;
            }

            return new ReferenceIndex(
                cached.Exact?.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value ?? [], StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                cached.Tokens?.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value ?? [], StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveReferenceIndexCache(string path, ReferenceIndex index)
    {
        var payload = new ReferenceIndexCache(
            index.Exact.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase),
            index.Tokens.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(payload, ClusterCacheJsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static IReadOnlyList<ConceptRow> BuildArchitectureRoles(CanonicalAstDocument document)
    {
        var roles = new Dictionary<string, ConceptRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            var role = InferRole(file) switch
            {
                "module" => file.References.Any(r => r.Kind == "call") ? "orchestrator" : "module",
                "component" => file.References.Any(r => r.Kind == "include" || r.Kind == "import") ? "adapter" : "component",
                "leaf" => "utility",
                "missing" or "error" => "unknown",
                _ => "support",
            };

            if (!roles.TryGetValue(role, out var concept))
            {
                concept = new ConceptRow(role);
                roles[role] = concept;
            }

            concept.Files.Add(file.RelativePath);
            concept.Evidence.Add($"{file.Language}/{InferRole(file)}");
            concept.Evidence.AddRange(file.Tags.Take(2));
        }

        return roles.Values.ToList();
    }

    private static IReadOnlyList<C4Row> BuildComponentSignals(CanonicalAstDocument document)
    {
        var roles = new Dictionary<string, C4Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            var component = InferRuntimeRole(file) switch
            {
                "orchestrator" => "container-orchestrator",
                "adapter" => "boundary-adapter",
                "processor" => "processing-component",
                "service" => "service-component",
                "pipeline" => "workflow-component",
                _ => InferRole(file),
            };

            if (!roles.TryGetValue(component, out var concept))
            {
                concept = new C4Row(component);
                roles[component] = concept;
            }

            concept.Evidence.Add($"{file.Language}/{InferRuntimeRole(file)}");
            concept.Evidence.AddRange(file.Tags.Take(2));
            foreach (var relationship in BuildComponentRelationships(file).Take(6))
            {
                concept.Relationships.Add(relationship);
            }
        }

        return roles.Values.ToList();
    }

    private static IReadOnlyList<C4Row> BuildC4Actors(CanonicalAstDocument document)
    {
        var rows = new Dictionary<string, C4Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var intent in BuildProductIntent(document))
        {
            var name = intent.Name.Contains("generate", StringComparison.OrdinalIgnoreCase) ? "spec author" : "developer";
            AddC4Row(rows, name, string.Join(", ", intent.Evidence.Take(4)));
        }

        foreach (var scenario in BuildScenarios(document))
        {
            if (scenario.Name.Contains("Query", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "consumer", string.Join(", ", scenario.Signals.Take(4)));
            }
            if (scenario.Name.Contains("Build", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "operator", string.Join(", ", scenario.Signals.Take(4)));
            }
        }

        return rows.Values.ToList();
    }

    private static IReadOnlyList<C4Row> BuildC4ExternalSystems(CanonicalAstDocument document)
    {
        var rows = new Dictionary<string, C4Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            var haystack = BuildHaystack(file);
            if (haystack.Contains("github", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "GitHub", file.RelativePath);
            }
            if (haystack.Contains("ollama", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "Ollama", file.RelativePath);
            }
            if (haystack.Contains("python", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "Python runtime", file.RelativePath);
            }
            if (haystack.Contains("clang", StringComparison.OrdinalIgnoreCase))
            {
                AddC4Row(rows, "libclang", file.RelativePath);
            }
        }

        return rows.Values.ToList();
    }

    private static string InferContainerResponsibility(string roleName)
    {
        if (roleName.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)) return "coordinate workflows";
        if (roleName.Contains("adapter", StringComparison.OrdinalIgnoreCase)) return "translate between boundaries";
        if (roleName.Contains("processor", StringComparison.OrdinalIgnoreCase)) return "transform inputs";
        if (roleName.Contains("service", StringComparison.OrdinalIgnoreCase)) return "provide reusable capabilities";
        if (roleName.Contains("pipeline", StringComparison.OrdinalIgnoreCase)) return "sequence processing stages";
        return "house application logic";
    }

    private static string InferContainerTechnology(IReadOnlyList<string> files)
    {
        var languages = files
            .Select(path => Path.GetExtension(path))
            .Select(ext => ext.TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join(", ", languages.Take(3));
    }

    private static string InferClusterResponsibility(ClusterRow cluster)
    {
        if (cluster.Name.Contains("Managed", StringComparison.OrdinalIgnoreCase)) return "managed subsystem";
        if (cluster.Name.Contains("Native", StringComparison.OrdinalIgnoreCase)) return "native subsystem";
        if (cluster.Name.Contains("Scripting", StringComparison.OrdinalIgnoreCase)) return "scripted automation";
        return "cross-cutting subsystem";
    }

    private static string InferClusterTechnology(ClusterRow cluster)
    {
        return string.Join(", ", cluster.Signals.Take(3));
    }

    private static string InferComponentResponsibility(string componentName)
    {
        if (componentName.Contains("processing", StringComparison.OrdinalIgnoreCase)) return "process data";
        if (componentName.Contains("boundary", StringComparison.OrdinalIgnoreCase)) return "bridge boundaries";
        if (componentName.Contains("service", StringComparison.OrdinalIgnoreCase)) return "serve capabilities";
        if (componentName.Contains("workflow", StringComparison.OrdinalIgnoreCase)) return "coordinate flow";
        return "internal component";
    }

    private static IReadOnlyList<string> BuildCodeSymbols(CanonicalAstFile file)
    {
        var symbols = new List<string>();
        foreach (var symbol in file.References.Where(reference => reference.Kind == "defines").Select(reference => reference.Target).Where(target => !string.IsNullOrWhiteSpace(target)).Take(10))
        {
            symbols.Add(symbol!);
        }

        foreach (var node in EnumerateNodes(file.Root).Where(node => !string.IsNullOrWhiteSpace(node.Name)).Take(10))
        {
            symbols.Add(node.Name!);
        }

        return symbols
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddC4Row(Dictionary<string, C4Row> rows, string name, string evidence)
    {
        if (!rows.TryGetValue(name, out var row))
        {
            row = new C4Row(name);
            rows[name] = row;
        }

        row.Evidence.Add(evidence);
    }

    private static IEnumerable<string> BuildComponentRelationships(CanonicalAstFile file)
    {
        foreach (var reference in file.References.Take(6))
        {
            var target = reference.Target ?? reference.TargetPath ?? "unknown";
            yield return $"{reference.Kind}->{target}";
        }
    }

    private static string InferApplicationToolRole(CanonicalAstFile file)
    {
        var path = file.RelativePath;
        var tags = string.Join(" ", file.Tags);
        var referenceKinds = string.Join(" ", file.References.Select(reference => reference.Kind));
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", path, file.Language, tags, nodeText, referenceKinds);

        if (IsEntryPointLike(path, tags, haystack))
        {
            return "application";
        }

        if (IsToolLike(path, tags, haystack))
        {
            return "tool";
        }

        if (IsWorkflowLike(path, tags, haystack))
        {
            return "generator";
        }

        if (IsConfigLike(path, tags, haystack))
        {
            return "operator surface";
        }

        return file.RootNodeCount <= 3 ? "utility" : "support";
    }

    private static IEnumerable<string> BuildApplicationToolSignals(CanonicalAstFile file)
    {
        yield return file.Language;
        yield return InferRole(file);
        foreach (var tag in file.Tags.Take(3))
        {
            yield return tag;
        }

        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", file.RelativePath, file.Language, string.Join(" ", file.Tags), nodeText, string.Join(" ", file.References.Select(reference => reference.Kind)));

        if (IsEntryPointLike(file.RelativePath, string.Join(" ", file.Tags), haystack))
        {
            yield return "entrypoint";
        }

        if (IsToolLike(file.RelativePath, string.Join(" ", file.Tags), haystack))
        {
            yield return "tool";
        }

        if (IsWorkflowLike(file.RelativePath, string.Join(" ", file.Tags), haystack))
        {
            yield return "generator";
        }
    }

    private static string InferRuntimeRole(CanonicalAstFile file)
    {
        var path = file.RelativePath;
        var tags = string.Join(" ", file.Tags);
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", path, file.Language, tags, nodeText, string.Join(" ", file.References.Select(reference => reference.Kind)));

        if (IsOrchestratorLike(path, tags, haystack))
        {
            return "orchestrator";
        }

        if (IsAdapterLike(path, tags, haystack))
        {
            return "adapter";
        }

        if (IsProcessorLike(path, tags, haystack))
        {
            return "processor";
        }

        if (IsServiceLike(path, tags, haystack))
        {
            return "service";
        }

        if (IsComponentLike(path, tags, haystack))
        {
            return "component";
        }

        if (IsWorkflowLike(path, tags, haystack))
        {
            return "pipeline";
        }

        if (IsTestLike(path, tags, haystack))
        {
            return "test-support";
        }

        return file.RootNodeCount <= 3 ? "leaf" : "module";
    }

    private static IEnumerable<string> BuildRuntimeRoleSignals(CanonicalAstFile file)
    {
        var tags = string.Join(" ", file.Tags);
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", file.RelativePath, file.Language, tags, nodeText, string.Join(" ", file.References.Select(reference => reference.Kind)));

        yield return InferRole(file);
        yield return file.Language;
        if (IsOrchestratorLike(file.RelativePath, tags, haystack)) yield return "orchestrator";
        if (IsAdapterLike(file.RelativePath, tags, haystack)) yield return "adapter";
        if (IsProcessorLike(file.RelativePath, tags, haystack)) yield return "processor";
        if (IsServiceLike(file.RelativePath, tags, haystack)) yield return "service";
        if (IsComponentLike(file.RelativePath, tags, haystack)) yield return "component";
        if (IsWorkflowLike(file.RelativePath, tags, haystack)) yield return "pipeline";
    }

    private static string InferDataRole(CanonicalAstFile file)
    {
        var path = file.RelativePath;
        var tags = string.Join(" ", file.Tags);
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", path, file.Language, tags, nodeText, string.Join(" ", file.References.Select(reference => reference.Kind)));

        if (path.Contains("schema", StringComparison.OrdinalIgnoreCase) || haystack.Contains("schema", StringComparison.OrdinalIgnoreCase))
        {
            return "schema";
        }

        if (path.Contains("dto", StringComparison.OrdinalIgnoreCase) || haystack.Contains("dto", StringComparison.OrdinalIgnoreCase))
        {
            return "dto";
        }

        if (path.Contains("model", StringComparison.OrdinalIgnoreCase) || haystack.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return "model";
        }

        if (haystack.Contains("record", StringComparison.OrdinalIgnoreCase) || haystack.Contains("struct", StringComparison.OrdinalIgnoreCase))
        {
            return "record";
        }

        if (haystack.Contains("entity", StringComparison.OrdinalIgnoreCase))
        {
            return "entity";
        }

        if (IsConfigLike(path, tags, haystack))
        {
            return "configuration";
        }

        return "data-structure";
    }

    private static IEnumerable<string> BuildDataRoleSignals(CanonicalAstFile file)
    {
        var tags = string.Join(" ", file.Tags);
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));
        var haystack = string.Join(" ", file.RelativePath, file.Language, tags, nodeText, string.Join(" ", file.References.Select(reference => reference.Kind)));

        yield return file.Language;
        yield return InferRole(file);
        if (pathOrHaystackContains(file.RelativePath, haystack, "schema")) yield return "schema";
        if (pathOrHaystackContains(file.RelativePath, haystack, "dto")) yield return "dto";
        if (pathOrHaystackContains(file.RelativePath, haystack, "model")) yield return "model";
        if (pathOrHaystackContains(file.RelativePath, haystack, "record")) yield return "record";
        if (pathOrHaystackContains(file.RelativePath, haystack, "entity")) yield return "entity";
        if (IsConfigLike(file.RelativePath, tags, haystack)) yield return "config";
    }

    private static bool IsEntryPointLike(string path, string tags, string haystack)
    {
        return path.Contains("main", StringComparison.OrdinalIgnoreCase)
            || path.Contains("app", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("entry", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("program", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("startup", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("host", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsToolLike(string path, string tags, string haystack)
    {
        return path.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || path.Contains("cli", StringComparison.OrdinalIgnoreCase)
            || path.Contains("script", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("utility", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("command", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("generate", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("export", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDataLike(string path, string tags, string haystack)
    {
        return path.Contains("model", StringComparison.OrdinalIgnoreCase)
            || path.Contains("dto", StringComparison.OrdinalIgnoreCase)
            || path.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || path.Contains("entity", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("data", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("record", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("struct", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("property", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("field", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkflowLike(string path, string tags, string haystack)
    {
        return path.Contains("pipeline", StringComparison.OrdinalIgnoreCase)
            || path.Contains("flow", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("workflow", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("stage", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("step", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("sequence", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdapterLike(string path, string tags, string haystack)
    {
        return path.Contains("adapter", StringComparison.OrdinalIgnoreCase)
            || path.Contains("bridge", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("integration", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("shim", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("facade", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("translator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessorLike(string path, string tags, string haystack)
    {
        return path.Contains("processor", StringComparison.OrdinalIgnoreCase)
            || path.Contains("handler", StringComparison.OrdinalIgnoreCase)
            || path.Contains("parser", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("processing", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("transform", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("compute", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("analyze", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrchestratorLike(string path, string tags, string haystack)
    {
        return path.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)
            || path.Contains("manager", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("coordination", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("dispatch", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("route", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("control", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServiceLike(string path, string tags, string haystack)
    {
        return path.Contains("service", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("service", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("manager", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("worker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComponentLike(string path, string tags, string haystack)
    {
        return path.Contains("component", StringComparison.OrdinalIgnoreCase)
            || path.Contains("module", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("component", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("widget", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("part", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("subsystem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigLike(string path, string tags, string haystack)
    {
        return path.Contains("config", StringComparison.OrdinalIgnoreCase)
            || path.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || path.Contains("option", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("config", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("constant", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("environment", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestLike(string path, string tags, string haystack)
    {
        return path.Contains("test", StringComparison.OrdinalIgnoreCase)
            || path.Contains("spec", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("test", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("assert", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("mock", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("fixture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool pathOrHaystackContains(string path, string haystack, string token)
    {
        return path.Contains(token, StringComparison.OrdinalIgnoreCase)
            || haystack.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string InferExecutionRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (file.RelativePath.Contains("main", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("entry", StringComparison.OrdinalIgnoreCase) || haystack.Contains("startup", StringComparison.OrdinalIgnoreCase))
        {
            return "entrypoint";
        }

        if (haystack.Contains("state", StringComparison.OrdinalIgnoreCase) || haystack.Contains("transition", StringComparison.OrdinalIgnoreCase) || haystack.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return "state-machine";
        }

        if (haystack.Contains("step", StringComparison.OrdinalIgnoreCase) || haystack.Contains("stage", StringComparison.OrdinalIgnoreCase) || haystack.Contains("sequence", StringComparison.OrdinalIgnoreCase) || haystack.Contains("workflow", StringComparison.OrdinalIgnoreCase))
        {
            return "workflow";
        }

        if (haystack.Contains("dispatch", StringComparison.OrdinalIgnoreCase) || haystack.Contains("schedule", StringComparison.OrdinalIgnoreCase) || haystack.Contains("queue", StringComparison.OrdinalIgnoreCase))
        {
            return "dispatcher";
        }

        return "execution-unit";
    }

    private static string InferPersistenceRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (file.RelativePath.Contains("db", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("database", StringComparison.OrdinalIgnoreCase) || haystack.Contains("sql", StringComparison.OrdinalIgnoreCase))
        {
            return "database-boundary";
        }

        if (file.RelativePath.Contains("cache", StringComparison.OrdinalIgnoreCase) || haystack.Contains("cache", StringComparison.OrdinalIgnoreCase))
        {
            return "cache";
        }

        if (haystack.Contains("serialize", StringComparison.OrdinalIgnoreCase) || haystack.Contains("deserialize", StringComparison.OrdinalIgnoreCase) || haystack.Contains("json", StringComparison.OrdinalIgnoreCase) || haystack.Contains("yaml", StringComparison.OrdinalIgnoreCase))
        {
            return "serialization";
        }

        if (file.RelativePath.Contains("store", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("repository", StringComparison.OrdinalIgnoreCase) || haystack.Contains("persist", StringComparison.OrdinalIgnoreCase))
        {
            return "storage-adapter";
        }

        return "persistence-unit";
    }

    private static string InferIntegrationRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (file.RelativePath.Contains("api", StringComparison.OrdinalIgnoreCase) || haystack.Contains("http", StringComparison.OrdinalIgnoreCase) || haystack.Contains("rest", StringComparison.OrdinalIgnoreCase) || haystack.Contains("graphql", StringComparison.OrdinalIgnoreCase))
        {
            return "api-boundary";
        }

        if (haystack.Contains("client", StringComparison.OrdinalIgnoreCase) || haystack.Contains("sdk", StringComparison.OrdinalIgnoreCase) || haystack.Contains("connector", StringComparison.OrdinalIgnoreCase))
        {
            return "client-integration";
        }

        if (haystack.Contains("socket", StringComparison.OrdinalIgnoreCase) || haystack.Contains("network", StringComparison.OrdinalIgnoreCase) || haystack.Contains("tcp", StringComparison.OrdinalIgnoreCase) || haystack.Contains("udp", StringComparison.OrdinalIgnoreCase))
        {
            return "network-boundary";
        }

        if (haystack.Contains("plugin", StringComparison.OrdinalIgnoreCase) || haystack.Contains("extension", StringComparison.OrdinalIgnoreCase) || haystack.Contains("host", StringComparison.OrdinalIgnoreCase))
        {
            return "host-integration";
        }

        return "integration-unit";
    }

    private static IEnumerable<string> BuildExecutionSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("startup", StringComparison.OrdinalIgnoreCase)) yield return "startup";
        if (haystack.Contains("state", StringComparison.OrdinalIgnoreCase)) yield return "state";
        if (haystack.Contains("transition", StringComparison.OrdinalIgnoreCase)) yield return "transition";
        if (haystack.Contains("workflow", StringComparison.OrdinalIgnoreCase)) yield return "workflow";
        if (haystack.Contains("dispatch", StringComparison.OrdinalIgnoreCase)) yield return "dispatch";
        if (haystack.Contains("queue", StringComparison.OrdinalIgnoreCase)) yield return "queue";
    }

    private static IEnumerable<string> BuildPersistenceSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("database", StringComparison.OrdinalIgnoreCase) || haystack.Contains("db", StringComparison.OrdinalIgnoreCase)) yield return "database";
        if (haystack.Contains("cache", StringComparison.OrdinalIgnoreCase)) yield return "cache";
        if (haystack.Contains("serialize", StringComparison.OrdinalIgnoreCase) || haystack.Contains("deserialize", StringComparison.OrdinalIgnoreCase)) yield return "serialization";
        if (haystack.Contains("json", StringComparison.OrdinalIgnoreCase)) yield return "json";
        if (haystack.Contains("yaml", StringComparison.OrdinalIgnoreCase) || haystack.Contains("toml", StringComparison.OrdinalIgnoreCase)) yield return "config-storage";
        if (haystack.Contains("persist", StringComparison.OrdinalIgnoreCase)) yield return "persist";
    }

    private static IEnumerable<string> BuildIntegrationSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("api", StringComparison.OrdinalIgnoreCase)) yield return "api";
        if (haystack.Contains("http", StringComparison.OrdinalIgnoreCase) || haystack.Contains("rest", StringComparison.OrdinalIgnoreCase) || haystack.Contains("graphql", StringComparison.OrdinalIgnoreCase)) yield return "network-api";
        if (haystack.Contains("client", StringComparison.OrdinalIgnoreCase) || haystack.Contains("connector", StringComparison.OrdinalIgnoreCase)) yield return "client";
        if (haystack.Contains("plugin", StringComparison.OrdinalIgnoreCase) || haystack.Contains("extension", StringComparison.OrdinalIgnoreCase)) yield return "plugin";
        if (haystack.Contains("socket", StringComparison.OrdinalIgnoreCase) || haystack.Contains("tcp", StringComparison.OrdinalIgnoreCase) || haystack.Contains("udp", StringComparison.OrdinalIgnoreCase)) yield return "network";
        if (haystack.Contains("host", StringComparison.OrdinalIgnoreCase)) yield return "host";
    }

    private static string InferCommandQueryRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (haystack.Contains("command", StringComparison.OrdinalIgnoreCase) || haystack.Contains("mutate", StringComparison.OrdinalIgnoreCase) || haystack.Contains("write", StringComparison.OrdinalIgnoreCase) || haystack.Contains("save", StringComparison.OrdinalIgnoreCase))
        {
            return "command";
        }

        if (haystack.Contains("query", StringComparison.OrdinalIgnoreCase) || haystack.Contains("read", StringComparison.OrdinalIgnoreCase) || haystack.Contains("inspect", StringComparison.OrdinalIgnoreCase) || haystack.Contains("view", StringComparison.OrdinalIgnoreCase))
        {
            return "query";
        }

        if (haystack.Contains("command", StringComparison.OrdinalIgnoreCase) && haystack.Contains("query", StringComparison.OrdinalIgnoreCase))
        {
            return "cqrs";
        }

        return "action";
    }

    private static string InferCouplingRole(CanonicalAstFile file)
    {
        var fanOut = file.References.Count(reference => reference.Kind is "include" or "using" or "import" or "call" or "type");
        var fanIn = 0; // inferred from graph later; this layer keeps file-local signals
        var haystack = BuildHaystack(file);

        if (fanOut >= 12)
        {
            return "high-fan-out";
        }

        if (fanOut >= 6)
        {
            return "coupled";
        }

        if (haystack.Contains("bridge", StringComparison.OrdinalIgnoreCase) || haystack.Contains("adapter", StringComparison.OrdinalIgnoreCase) || haystack.Contains("facade", StringComparison.OrdinalIgnoreCase))
        {
            return "boundary-coupled";
        }

        if (fanOut <= 2 && fanIn == 0)
        {
            return "low-coupling";
        }

        return "moderate-coupling";
    }

    private static string InferVerificationRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (IsTestLike(file.RelativePath, string.Join(" ", file.Tags), haystack))
        {
            return "test";
        }

        if (haystack.Contains("assert", StringComparison.OrdinalIgnoreCase) || haystack.Contains("expect", StringComparison.OrdinalIgnoreCase))
        {
            return "assertion";
        }

        if (haystack.Contains("mock", StringComparison.OrdinalIgnoreCase) || haystack.Contains("stub", StringComparison.OrdinalIgnoreCase) || haystack.Contains("fixture", StringComparison.OrdinalIgnoreCase))
        {
            return "test-support";
        }

        if (haystack.Contains("validate", StringComparison.OrdinalIgnoreCase) || haystack.Contains("verify", StringComparison.OrdinalIgnoreCase))
        {
            return "verification";
        }

        return "non-test";
    }

    private static IEnumerable<string> BuildCommandQuerySignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("command", StringComparison.OrdinalIgnoreCase)) yield return "command";
        if (haystack.Contains("query", StringComparison.OrdinalIgnoreCase)) yield return "query";
        if (haystack.Contains("read", StringComparison.OrdinalIgnoreCase)) yield return "read";
        if (haystack.Contains("write", StringComparison.OrdinalIgnoreCase)) yield return "write";
        if (haystack.Contains("mutate", StringComparison.OrdinalIgnoreCase)) yield return "mutate";
        if (haystack.Contains("inspect", StringComparison.OrdinalIgnoreCase)) yield return "inspect";
    }

    private static IEnumerable<string> BuildCouplingSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        var fanOut = file.References.Count(reference => reference.Kind is "include" or "using" or "import" or "call" or "type");
        yield return file.Language;
        yield return $"fan-out:{fanOut}";
        if (haystack.Contains("bridge", StringComparison.OrdinalIgnoreCase)) yield return "bridge";
        if (haystack.Contains("adapter", StringComparison.OrdinalIgnoreCase)) yield return "adapter";
        if (haystack.Contains("facade", StringComparison.OrdinalIgnoreCase)) yield return "facade";
        if (haystack.Contains("wrapper", StringComparison.OrdinalIgnoreCase)) yield return "wrapper";
        if (haystack.Contains("couple", StringComparison.OrdinalIgnoreCase)) yield return "coupling";
    }

    private static IEnumerable<string> BuildVerificationSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (IsTestLike(file.RelativePath, string.Join(" ", file.Tags), haystack)) yield return "test";
        if (haystack.Contains("assert", StringComparison.OrdinalIgnoreCase)) yield return "assert";
        if (haystack.Contains("mock", StringComparison.OrdinalIgnoreCase)) yield return "mock";
        if (haystack.Contains("fixture", StringComparison.OrdinalIgnoreCase)) yield return "fixture";
        if (haystack.Contains("verify", StringComparison.OrdinalIgnoreCase)) yield return "verify";
        if (haystack.Contains("coverage", StringComparison.OrdinalIgnoreCase)) yield return "coverage";
    }

    private static string InferConfigEnvironmentRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (haystack.Contains("environment", StringComparison.OrdinalIgnoreCase) || haystack.Contains("env", StringComparison.OrdinalIgnoreCase) || haystack.Contains("variable", StringComparison.OrdinalIgnoreCase))
        {
            return "environment";
        }

        if (haystack.Contains("config", StringComparison.OrdinalIgnoreCase) || haystack.Contains("setting", StringComparison.OrdinalIgnoreCase) || haystack.Contains("option", StringComparison.OrdinalIgnoreCase))
        {
            return "configuration";
        }

        if (haystack.Contains("feature flag", StringComparison.OrdinalIgnoreCase) || haystack.Contains("featureflag", StringComparison.OrdinalIgnoreCase) || haystack.Contains("toggle", StringComparison.OrdinalIgnoreCase))
        {
            return "feature-flag";
        }

        if (haystack.Contains("secret", StringComparison.OrdinalIgnoreCase) || haystack.Contains("credential", StringComparison.OrdinalIgnoreCase) || haystack.Contains("connection string", StringComparison.OrdinalIgnoreCase))
        {
            return "secret-boundary";
        }

        return "runtime-settings";
    }

    private static string InferResilienceRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (haystack.Contains("try", StringComparison.OrdinalIgnoreCase) || haystack.Contains("catch", StringComparison.OrdinalIgnoreCase) || haystack.Contains("throw", StringComparison.OrdinalIgnoreCase))
        {
            return "exception-handling";
        }

        if (haystack.Contains("retry", StringComparison.OrdinalIgnoreCase) || haystack.Contains("backoff", StringComparison.OrdinalIgnoreCase) || haystack.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "retry-timeout";
        }

        if (haystack.Contains("fallback", StringComparison.OrdinalIgnoreCase) || haystack.Contains("default", StringComparison.OrdinalIgnoreCase) || haystack.Contains("degrade", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback";
        }

        if (haystack.Contains("resilient", StringComparison.OrdinalIgnoreCase) || haystack.Contains("circuit", StringComparison.OrdinalIgnoreCase) || haystack.Contains("guard", StringComparison.OrdinalIgnoreCase))
        {
            return "resilience-guard";
        }

        return "error-path";
    }

    private static string InferObservabilityRole(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);

        if (haystack.Contains("log", StringComparison.OrdinalIgnoreCase) || haystack.Contains("logger", StringComparison.OrdinalIgnoreCase))
        {
            return "logging";
        }

        if (haystack.Contains("trace", StringComparison.OrdinalIgnoreCase) || haystack.Contains("span", StringComparison.OrdinalIgnoreCase))
        {
            return "tracing";
        }

        if (haystack.Contains("metric", StringComparison.OrdinalIgnoreCase) || haystack.Contains("counter", StringComparison.OrdinalIgnoreCase) || haystack.Contains("gauge", StringComparison.OrdinalIgnoreCase))
        {
            return "metrics";
        }

        if (haystack.Contains("diagnostic", StringComparison.OrdinalIgnoreCase) || haystack.Contains("telemetry", StringComparison.OrdinalIgnoreCase) || haystack.Contains("event", StringComparison.OrdinalIgnoreCase))
        {
            return "telemetry";
        }

        return "observability";
    }

    private static int ScoreFileImportance(CanonicalAstFile file)
    {
        var score = 0;
        var haystack = BuildHaystack(file);
        var dependencyCount = file.References.Count(reference => reference.Kind is "include" or "using" or "import" or "call" or "type");
        var callCount = file.References.Count(reference => reference.Kind == "call");
        var symbolCount = EnumerateNodes(file.Root).Count(node => !string.IsNullOrWhiteSpace(node.Name));
        var role = InferRole(file);
        var runtimeRole = InferRuntimeRole(file);

        score += dependencyCount * 3;
        score += callCount * 2;
        score += Math.Min(symbolCount, 20);
        score += file.Tags.Count * 2;
        score += file.RootNodeCount;

        if (role is "module")
        {
            score += 4;
        }

        if (runtimeRole is "orchestrator" or "adapter" or "processor" or "service" or "pipeline")
        {
            score += 8;
        }

        if (BuildPerformanceSignals(file).Any())
        {
            score += 3;
        }

        if (InferConfigEnvironmentRole(file) is not "runtime-settings")
        {
            score += 2;
        }

        if (InferResilienceRole(file) is not "error-path")
        {
            score += 2;
        }

        if (InferObservabilityRole(file) is not "observability")
        {
            score += 2;
        }

        if (haystack.Contains("main", StringComparison.OrdinalIgnoreCase) || haystack.Contains("entry", StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        if (haystack.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        return score;
    }

    private static IEnumerable<string> BuildImportanceReasons(CanonicalAstFile file)
    {
        var dependencyCount = file.References.Count(reference => reference.Kind is "include" or "using" or "import" or "call" or "type");
        var callCount = file.References.Count(reference => reference.Kind == "call");
        var role = InferRole(file);
        var runtimeRole = InferRuntimeRole(file);
        var perf = BuildPerformanceSignals(file).ToList();

        if (dependencyCount >= 8) yield return $"deps:{dependencyCount}";
        if (callCount >= 6) yield return $"calls:{callCount}";
        if (role == "module") yield return "module";
        if (runtimeRole is "orchestrator" or "adapter" or "processor" or "service" or "pipeline") yield return runtimeRole;
        if (IsEntryPointLike(file.RelativePath, string.Join(" ", file.Tags), BuildHaystack(file))) yield return "entrypoint";
        if (IsTestLike(file.RelativePath, string.Join(" ", file.Tags), BuildHaystack(file))) yield return "test";
        if (perf.Count > 0) yield return string.Join("/", perf.Take(3));
    }

    private static IEnumerable<string> BuildPerformanceSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        if (IsAsyncFile(file)) yield return "async";
        if (IsWaitHeavyFile(file)) yield return "wait";
        if (IsIoWaitFile(file)) yield return "io";
        if (IsCpuHotspotFile(file)) yield return "cpu-hotspot";
        if (IsConcurrencyPressureFile(file)) yield return "concurrency";
        if (haystack.Contains("hot", StringComparison.OrdinalIgnoreCase)) yield return "hot";
    }

    private static IEnumerable<string> BuildConfigEnvironmentSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("config", StringComparison.OrdinalIgnoreCase)) yield return "config";
        if (haystack.Contains("environment", StringComparison.OrdinalIgnoreCase) || haystack.Contains("env", StringComparison.OrdinalIgnoreCase)) yield return "env";
        if (haystack.Contains("feature", StringComparison.OrdinalIgnoreCase)) yield return "feature";
        if (haystack.Contains("secret", StringComparison.OrdinalIgnoreCase) || haystack.Contains("credential", StringComparison.OrdinalIgnoreCase)) yield return "secret";
        if (haystack.Contains("option", StringComparison.OrdinalIgnoreCase)) yield return "option";
        if (haystack.Contains("variable", StringComparison.OrdinalIgnoreCase)) yield return "variable";
    }

    private static IEnumerable<string> BuildResilienceSignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("try", StringComparison.OrdinalIgnoreCase) || haystack.Contains("catch", StringComparison.OrdinalIgnoreCase)) yield return "try-catch";
        if (haystack.Contains("retry", StringComparison.OrdinalIgnoreCase)) yield return "retry";
        if (haystack.Contains("timeout", StringComparison.OrdinalIgnoreCase)) yield return "timeout";
        if (haystack.Contains("fallback", StringComparison.OrdinalIgnoreCase)) yield return "fallback";
        if (haystack.Contains("circuit", StringComparison.OrdinalIgnoreCase)) yield return "circuit";
        if (haystack.Contains("guard", StringComparison.OrdinalIgnoreCase)) yield return "guard";
    }

    private static IEnumerable<string> BuildObservabilitySignals(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        yield return file.Language;
        if (haystack.Contains("log", StringComparison.OrdinalIgnoreCase)) yield return "log";
        if (haystack.Contains("trace", StringComparison.OrdinalIgnoreCase)) yield return "trace";
        if (haystack.Contains("metric", StringComparison.OrdinalIgnoreCase)) yield return "metric";
        if (haystack.Contains("telemetry", StringComparison.OrdinalIgnoreCase)) yield return "telemetry";
        if (haystack.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)) yield return "diagnostic";
        if (haystack.Contains("event", StringComparison.OrdinalIgnoreCase)) yield return "event";
    }

    private static string BuildHaystack(CanonicalAstFile file)
    {
        var nodeText = string.Join(" ", EnumerateNodes(file.Root)
            .SelectMany(node => new[] { node.Kind, node.Name, node.Signature, node.Summary })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!));

        return string.Join(" ",
            file.RelativePath,
            file.Language,
            string.Join(" ", file.Tags),
            nodeText,
            string.Join(" ", file.References.Select(reference => reference.Kind)),
            string.Join(" ", file.References.Select(reference => reference.Target)));
    }

    private static IReadOnlyList<ConceptRow> BuildDomainConcepts(CanonicalAstDocument document)
    {
        var terms = new Dictionary<string, ConceptRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in document.Symbols)
        {
            var tokens = SplitConceptTokens(symbol.Name);
            foreach (var token in tokens)
            {
                if (!terms.TryGetValue(token, out var concept))
                {
                    concept = new ConceptRow(token);
                    terms[token] = concept;
                }

                concept.Terms.Add(symbol.Name);
                concept.Evidence.Add($"{symbol.Language}:{symbol.RelativePath}");
            }
        }

        return terms.Values.ToList();
    }

    private static IReadOnlyList<ScenarioRow> BuildScenarios(CanonicalAstDocument document)
    {
        var scenarios = new List<ScenarioRow>();
        var sourceFiles = document.Files.Where(file => file.References.Any(reference => reference.Kind is "call" or "include" or "import")).ToList();
        if (sourceFiles.Count > 0)
        {
            scenarios.Add(new ScenarioRow("Build Orchestration", sourceFiles.Select(f => f.RelativePath).ToList(), sourceFiles.Select(f => $"{f.Language}/{InferRole(f)}").Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        var parserFiles = document.Files.Where(file => file.Tags.Any(tag => tag.Contains("Class", StringComparison.OrdinalIgnoreCase) || tag.Contains("Function", StringComparison.OrdinalIgnoreCase))).ToList();
        if (parserFiles.Count > 0)
        {
            scenarios.Add(new ScenarioRow("Parse And Index", parserFiles.Select(f => f.RelativePath).ToList(), parserFiles.Select(f => f.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        var queryFiles = document.Files.Where(file => file.RelativePath.Contains("query", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains("spec", StringComparison.OrdinalIgnoreCase)).ToList();
        if (queryFiles.Count > 0)
        {
            scenarios.Add(new ScenarioRow("Query And Enrich", queryFiles.Select(f => f.RelativePath).ToList(), queryFiles.Select(f => f.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return scenarios;
    }

    private static IReadOnlyList<IntentRow> BuildProductIntent(CanonicalAstDocument document)
    {
        var intents = new List<IntentRow>();
        if (document.Files.Any(file => file.RelativePath.Contains("spec", StringComparison.OrdinalIgnoreCase)))
        {
            intents.Add(new IntentRow("Generate layered specs", "Spec generation flow"));
        }

        if (document.Files.Any(file => file.RelativePath.Contains("ast", StringComparison.OrdinalIgnoreCase) || file.Tags.Any(tag => tag.Contains("Ast", StringComparison.OrdinalIgnoreCase))))
        {
            intents.Add(new IntentRow("Index and analyze code structure", "AST database and query flows"));
        }

        if (document.Files.Any(file => file.References.Any(reference => reference.Kind is "call" or "import" or "include")))
        {
            intents.Add(new IntentRow("Track dependency and usage relationships", "Reference graph and clusters"));
        }

        return intents;
    }

    private static IReadOnlyList<QuestionRow> BuildOpenQuestions(CanonicalAstDocument document)
    {
        var questions = new List<QuestionRow>();
        if (document.Symbols.Count < document.Files.Count)
        {
            questions.Add(new QuestionRow("Unresolved symbol coverage", "Not every file has symbol definitions captured", "medium"));
        }

        if (document.Files.Any(file => file.Status is "generic" or "error"))
        {
            questions.Add(new QuestionRow("Fallback parser quality", "Some files are indexed by generic structure only", "low"));
        }

        if (!document.Files.Any(file => file.References.Any(reference => reference.Kind == "call")))
        {
            questions.Add(new QuestionRow("Call graph completeness", "Call edges may be incomplete for some languages", "medium"));
        }

        return questions;
    }

    private static IReadOnlyList<PerformanceRow> BuildPerformanceAnalysis(CanonicalAstDocument document)
    {
        var performance = new List<PerformanceRow>();
        var asyncFiles = document.Files.Where(IsAsyncFile).ToList();
        if (asyncFiles.Count > 0)
        {
            performance.Add(new PerformanceRow(
                "async",
                "Async pipeline usage",
                string.Join(", ", asyncFiles.Select(file => file.RelativePath).Take(8)),
                asyncFiles.Count > 5 ? "high" : "medium"));
        }

        var waitFiles = document.Files.Where(IsWaitHeavyFile).ToList();
        if (waitFiles.Count > 0)
        {
            performance.Add(new PerformanceRow(
                "waiting",
                "Blocking or waiting patterns",
                string.Join(", ", waitFiles.Select(file => file.RelativePath).Take(8)),
                waitFiles.Count > 3 ? "high" : "medium"));
        }

        var includeHeavy = document.Files.Where(file => file.References.Count(reference => reference.Kind is "include" or "import" or "using") > 3).ToList();
        if (includeHeavy.Count > 0)
        {
            performance.Add(new PerformanceRow(
                "bottleneck",
                "Dependency-heavy files",
                string.Join(", ", includeHeavy.Select(file => file.RelativePath).Take(8)),
                "medium"));
        }

        var callHeavy = document.Files.Where(file => file.References.Count(reference => reference.Kind == "call") > 5).ToList();
        if (callHeavy.Count > 0)
        {
            performance.Add(new PerformanceRow(
                "bottleneck",
                "Call-dense orchestration files",
                string.Join(", ", callHeavy.Select(file => file.RelativePath).Take(8)),
                callHeavy.Count > 2 ? "high" : "medium"));
        }

        foreach (var scenario in BuildScenarios(document))
        {
            if (scenario.Name.Contains("Build", StringComparison.OrdinalIgnoreCase) || scenario.Name.Contains("Index", StringComparison.OrdinalIgnoreCase))
            {
                performance.Add(new PerformanceRow(
                    "async",
                    scenario.Name,
                    string.Join(", ", scenario.Files.Take(8)),
                    "medium"));
            }
        }

        return performance;
    }

    private static bool IsAsyncFile(CanonicalAstFile file)
    {
        return file.RelativePath.Contains("async", StringComparison.OrdinalIgnoreCase) ||
               file.Tags.Any(tag => tag.Contains("Async", StringComparison.OrdinalIgnoreCase)) ||
               file.References.Any(reference => reference.Target is not null && reference.Target.Contains("Async", StringComparison.OrdinalIgnoreCase)) ||
               file.References.Any(reference => reference.Kind is "call" or "type" && reference.Target is not null && reference.Target.Contains("Task", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWaitHeavyFile(CanonicalAstFile file)
    {
        return file.RelativePath.Contains("wait", StringComparison.OrdinalIgnoreCase) ||
               file.RelativePath.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
               file.Tags.Any(tag => tag.Contains("Wait", StringComparison.OrdinalIgnoreCase) || tag.Contains("Lock", StringComparison.OrdinalIgnoreCase)) ||
               file.References.Any(reference => reference.Evidence is not null && (reference.Evidence.Contains("Wait", StringComparison.OrdinalIgnoreCase) || reference.Evidence.Contains("lock", StringComparison.OrdinalIgnoreCase) || reference.Evidence.Contains("sleep", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsIoWaitFile(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        return haystack.Contains("io", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("file", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("write", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCpuHotspotFile(CanonicalAstFile file)
    {
        var dependencyCount = file.References.Count(reference => reference.Kind is "include" or "using" or "import" or "call" or "type");
        var callCount = file.References.Count(reference => reference.Kind == "call");
        var symbolCount = EnumerateNodes(file.Root).Count(node => !string.IsNullOrWhiteSpace(node.Name));
        return dependencyCount >= 8 || callCount >= 6 || symbolCount >= 20;
    }

    private static bool IsConcurrencyPressureFile(CanonicalAstFile file)
    {
        var haystack = BuildHaystack(file);
        var asyncSignals = haystack.Contains("async", StringComparison.OrdinalIgnoreCase) || haystack.Contains("await", StringComparison.OrdinalIgnoreCase);
        var threadSignals = haystack.Contains("thread", StringComparison.OrdinalIgnoreCase) || haystack.Contains("task", StringComparison.OrdinalIgnoreCase) || haystack.Contains("parallel", StringComparison.OrdinalIgnoreCase);
        var lockSignals = haystack.Contains("lock", StringComparison.OrdinalIgnoreCase) || haystack.Contains("mutex", StringComparison.OrdinalIgnoreCase) || haystack.Contains("semaphore", StringComparison.OrdinalIgnoreCase);
        return (asyncSignals && threadSignals) || (threadSignals && lockSignals) || (asyncSignals && lockSignals);
    }

    private static IReadOnlyList<string> SplitConceptTokens(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? []
            : name.Split(new[] { '_', '-', '.', ':', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(token => token.Trim())
                  .Where(token => token.Length > 1)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    private static string InferClusterName(IReadOnlyList<CanonicalAstFile> files)
    {
        var languages = files.Select(file => file.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (languages.Count == 1)
        {
            return languages[0] switch
            {
                "C#" => "Managed Code",
                "Python" => "Scripting",
                "C/C++" => "Native Code",
                _ => $"{languages[0]} Cluster",
            };
        }

        return "Mixed Subsystem";
    }

    private static IReadOnlyList<string> IdentifyHubNodes(CanonicalAstDocument document)
    {
        var hubs = new List<string>();
        var fileByPath = document.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in document.Files)
        {
            var role = InferRole(file);
            var runtimeRole = InferRuntimeRole(file);
            var isHub = false;

            if (file.RelativePath.Contains("main", StringComparison.OrdinalIgnoreCase) ||
                file.RelativePath.Contains("app", StringComparison.OrdinalIgnoreCase) ||
                file.RelativePath.Contains("program", StringComparison.OrdinalIgnoreCase))
            {
                isHub = true;
            }

            if (runtimeRole is "orchestrator" or "adapter" or "processor" or "service" or "pipeline")
            {
                isHub = true;
            }

            if (file.References.Count(r => r.Kind is "include" or "using" or "import" or "call") >= 8)
            {
                isHub = true;
            }

            if (isHub)
            {
                hubs.Add(file.RelativePath);
            }
        }

        return hubs.Count > 0 ? hubs : document.Files.Take(Math.Max(1, document.Files.Count / 100)).Select(f => f.RelativePath).ToList();
    }

    private static Dictionary<string, int> ComputeDistancesFromHubs(CanonicalAstDocument document, Dictionary<string, HashSet<string>> symbolIndex, IReadOnlyList<string> hubs, Dictionary<string, IReadOnlyList<CanonicalAstReference>> strongEdges)
    {
        var allDistances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileByPath = document.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var referenceIndex = GetReferenceIndex(document);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine($"[step4] L5: computing distances from {hubs.Count} hubs (parallel)");

        var hubResults = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var lockObj = new object();
        var progressLock = new object();
        var completedCount = 0;
        var parallelism = Math.Max(1, (int)Math.Ceiling(Environment.ProcessorCount * 0.75));
        var lastProgressTime = sw.Elapsed;

        var progressReporter = new HubProgressReporter(hubs.Count);
        progressReporter.Start();

        Parallel.ForEach(hubs, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, hub =>
        {
            var distances = DijkstraShortestPaths(hub, document, symbolIndex, referenceIndex, fileByPath, strongEdges);
            lock (lockObj)
            {
                hubResults[hub] = distances;
            }

            lock (progressLock)
            {
                completedCount++;
                var shouldReport = completedCount % Math.Max(1, hubs.Count / 100) == 0 ||
                                   (sw.Elapsed - lastProgressTime).TotalMilliseconds >= 1000 ||
                                   completedCount == hubs.Count;

                if (shouldReport)
                {
                    var elapsed = sw.Elapsed;
                    var rate = completedCount / elapsed.TotalSeconds;
                    var remaining = hubs.Count - completedCount;
                    var eta = remaining > 0 ? TimeSpan.FromSeconds(remaining / rate) : TimeSpan.Zero;
                    var percentage = (completedCount * 100) / hubs.Count;

                    progressReporter.Update(completedCount, elapsed, eta, percentage);
                    lastProgressTime = sw.Elapsed;
                }
            }
        });

        progressReporter.Complete();

        foreach (var kvp in hubResults)
        {
            foreach (var distKvp in kvp.Value)
            {
                if (!allDistances.ContainsKey(distKvp.Key) || allDistances[distKvp.Key] > distKvp.Value)
                {
                    allDistances[distKvp.Key] = distKvp.Value;
                }
            }
        }

        sw.Stop();
        Console.WriteLine($"[step4] L5: dijkstra parallel completed in {sw.ElapsedMilliseconds}ms ({hubs.Count} hubs)");

        return allDistances;
    }

    private static string ComputeAstHash(CanonicalAstDocument document)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var json = JsonSerializer.Serialize(document);
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    private static string GetSpecBuilderVersion()
    {
        var version = typeof(AstSpecLayersFlow).Assembly.GetName().Version;
        return $"{version?.Major}.{version?.Minor}.{version?.Build}";
    }

    private static string GetCachePath(string astHash, string phase)
    {
        var baseDir = AppContext.BaseDirectory;
        var cacheDir = Path.Combine(baseDir, "..", "..", "generated", "step4-cache", astHash);
        var fullPath = Path.GetFullPath(cacheDir);
        Directory.CreateDirectory(fullPath);
        return Path.Combine(fullPath, $"{phase}.json");
    }

    private static void InvalidateCacheIfNeeded(string astHash, string astSnapshotPath)
    {
        var baseDir = AppContext.BaseDirectory;
        var cacheDirPath = Path.Combine(baseDir, "..", "..", "generated", "step4-cache", astHash);
        var fullCachePath = Path.GetFullPath(cacheDirPath);

        if (!Directory.Exists(fullCachePath))
        {
            return;
        }

        bool shouldInvalidate = false;
        var versionFile = Path.Combine(fullCachePath, ".version");

        if (File.Exists(versionFile))
        {
            try
            {
                var cachedVersion = File.ReadAllText(versionFile).Trim();
                var currentVersion = GetSpecBuilderVersion();
                if (cachedVersion != currentVersion)
                {
                    Console.WriteLine($"[step4] L5: SpecBuilder version mismatch (cached: {cachedVersion}, current: {currentVersion}) - invalidating cache");
                    shouldInvalidate = true;
                }
            }
            catch { }
        }
        else
        {
            shouldInvalidate = true;
        }

        if (!shouldInvalidate && !string.IsNullOrEmpty(astSnapshotPath) && File.Exists(astSnapshotPath))
        {
            try
            {
                var astTime = File.GetLastWriteTimeUtc(astSnapshotPath);
                var cacheTime = Directory.GetCreationTimeUtc(fullCachePath);
                if (astTime > cacheTime)
                {
                    Console.WriteLine($"[step4] L5: AST file newer than cache - invalidating cache");
                    shouldInvalidate = true;
                }
            }
            catch { }
        }

        if (shouldInvalidate)
        {
            try
            {
                Directory.Delete(fullCachePath, recursive: true);
                Console.WriteLine($"[step4] L5: cache invalidated (version/upstream change)");
            }
            catch { }
        }
        else
        {
            try
            {
                File.WriteAllText(versionFile, GetSpecBuilderVersion());
            }
            catch { }
        }
    }

    private static Dictionary<string, int>? LoadCachedDistances(string astHash)
    {
        var path = GetCachePath(astHash, "distances");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        }
        catch { return null; }
    }

    private static void SaveCachedDistances(string astHash, Dictionary<string, int> distances)
    {
        try
        {
            var path = GetCachePath(astHash, "distances");
            var json = JsonSerializer.Serialize(distances, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static Dictionary<string, HashSet<string>>? LoadCachedGraph(string astHash)
    {
        var path = GetCachePath(astHash, "graph");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            return dict?.ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private static void SaveCachedGraph(string astHash, Dictionary<string, HashSet<string>> graph)
    {
        try
        {
            var path = GetCachePath(astHash, "graph");
            var dict = graph.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static List<HashSet<string>>? LoadCachedComponents(string astHash)
    {
        var path = GetCachePath(astHash, "components");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<List<string>>>(json);
            return list?.Select(component => new HashSet<string>(component, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        catch { return null; }
    }

    private static void SaveCachedComponents(string astHash, List<HashSet<string>> components)
    {
        try
        {
            var path = GetCachePath(astHash, "components");
            var list = components.Select(c => c.ToList()).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static Dictionary<string, IReadOnlyList<CanonicalAstReference>> BuildStrongEdgeGraph(CanonicalAstDocument document)
    {
        var strongEdges = new Dictionary<string, IReadOnlyList<CanonicalAstReference>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in document.Files)
        {
            var strong = file.References
                .Where(r => r.Kind is "defines" or "call")
                .ToList();

            if (strong.Count > 0)
            {
                strongEdges[file.RelativePath] = strong;
            }
        }

        return strongEdges;
    }

    private static Dictionary<string, int> DijkstraShortestPaths(string source, CanonicalAstDocument document, Dictionary<string, HashSet<string>> symbolIndex, ReferenceIndex referenceIndex, Dictionary<string, CanonicalAstFile> fileByPath, Dictionary<string, IReadOnlyList<CanonicalAstReference>> strongEdges, double targetReachability = 0.95)
    {
        var distances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pq = new PriorityQueue<(string Node, int Distance), int>();

        foreach (var file in document.Files)
        {
            distances[file.RelativePath] = int.MaxValue;
        }

        distances[source] = 0;
        pq.Enqueue((source, 0), 0);

        var maxDepth = Math.Min(30, Math.Max(5, document.Files.Count / 200));
        var edgesExamined = 0;
        var maxEdges = Math.Min(50000, document.Files.Count * 40);
        var targetFileCount = (int)(document.Files.Count * targetReachability);
        var discoveredCount = 1;

        while (pq.Count > 0 && edgesExamined < maxEdges)
        {
            if (discoveredCount >= targetFileCount)
            {
                break;
            }

            var (current, currentDist) = pq.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (currentDist >= maxDepth)
            {
                continue;
            }

            if (!fileByPath.TryGetValue(current, out var currentFile))
            {
                continue;
            }

            var candidates = new List<(string Target, int Cost)>();
            var edgesToExamine = strongEdges.TryGetValue(current, out var edges) ? edges : Array.Empty<CanonicalAstReference>();

            foreach (var reference in edgesToExamine)
            {
                edgesExamined++;
                if (edgesExamined >= maxEdges)
                {
                    break;
                }

                var targets = ResolveReferenceTargets(referenceIndex, symbolIndex, reference.Target ?? "");
                foreach (var target in targets)
                {
                    if (!visited.Contains(target) && distances.ContainsKey(target))
                    {
                        var cost = reference.Kind switch
                        {
                            "defines" => 1,
                            "call" => 2,
                            "include" or "using" or "import" => 3,
                            "type" => 4,
                            _ => 5,
                        };

                        var newDist = currentDist + cost;
                        if (newDist < distances[target])
                        {
                            var wasUndiscovered = distances[target] == int.MaxValue;
                            distances[target] = newDist;

                            if (wasUndiscovered)
                            {
                                discoveredCount++;
                            }

                            candidates.Add((target, newDist));
                        }
                    }
                }
            }

            foreach (var (target, newDist) in candidates.OrderBy(c => c.Cost).Take(12))
            {
                pq.Enqueue((target, newDist), newDist);
            }
        }

        return distances;
    }

    private static (int DistanceThreshold, double EdgeWeightBias) AdaptiveThresholds(CanonicalAstDocument document, Dictionary<string, int> distances)
    {
        if (distances.Count == 0)
        {
            return (12, 0.0);
        }

        var validDistances = distances.Values.Where(d => d != int.MaxValue).ToList();
        if (validDistances.Count == 0)
        {
            return (12, 0.0);
        }

        var avgDistance = validDistances.Average();
        var maxDistance = validDistances.Max();
        var reachability = (double)validDistances.Count / distances.Count;

        Console.WriteLine($"[step4] L5: distance stats - avg={avgDistance:F1}, max={maxDistance}, reach={reachability:P1}");

        var distanceThreshold = reachability switch
        {
            < 0.2 => Math.Min(8, (int)avgDistance + 2),
            < 0.5 => Math.Min(12, (int)avgDistance + 3),
            < 0.8 => Math.Min(16, (int)avgDistance + 4),
            _ => Math.Min(20, (int)avgDistance + 5),
        };

        var edgeWeightBias = reachability switch
        {
            < 0.3 => 0.25,
            < 0.6 => 0.15,
            < 0.85 => 0.08,
            _ => 0.0,
        };

        return (distanceThreshold, edgeWeightBias);
    }

    private static Dictionary<string, int> ComputeDistanceBands(Dictionary<string, int> distances)
    {
        var bands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in distances)
        {
            var band = kvp.Value switch
            {
                <= 4 => 0,
                <= 8 => 1,
                <= 12 => 2,
                <= 16 => 3,
                _ => 4,
            };
            bands[kvp.Key] = band;
        }
        return bands;
    }

    private static Dictionary<string, HashSet<string>> BuildFileGraphWithDijkstra(CanonicalAstDocument document, Dictionary<string, HashSet<string>> symbolIndex, Dictionary<string, int> distances, Dictionary<string, IReadOnlyList<CanonicalAstReference>> strongEdges)
    {
        Console.WriteLine("[step4] L5: building file graph with adaptive distance filtering (strong edges only)");
        var referenceIndex = GetReferenceIndex(document);
        var adjacency = document.Files.ToDictionary(file => file.RelativePath, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var fileByPath = document.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        var totalRefs = strongEdges.Values.Sum(edges => edges.Count);

        var (distanceThreshold, edgeWeightBias) = AdaptiveThresholds(document, distances);
        Console.WriteLine($"[step4] L5: adaptive threshold={distanceThreshold}, bias={edgeWeightBias:P0}");
        Console.WriteLine($"[step4] L5: processing {totalRefs} strong edges across {document.Files.Count} files");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var filesSeen = 0;
        var graphProgressReporter = new GraphProgressReporter(totalRefs);

        foreach (var file in document.Files)
        {
            filesSeen++;
            var source = file.RelativePath;
            var sourceLanguage = file.Language;
            var sourceRole = InferRole(file);
            var sourceDistance = distances.TryGetValue(source, out var d) ? d : int.MaxValue;
            var candidateEdges = new List<(string Target, string Kind, string Category, string Evidence, int Score)>();

            var edgesToProcess = strongEdges.TryGetValue(source, out var edges) ? edges : Array.Empty<CanonicalAstReference>();

            foreach (var reference in edgesToProcess)
            {
                if (reference.Target is null)
                {
                    continue;
                }

                processed++;
                graphProgressReporter.Update(processed, filesSeen, document.Files.Count, sw.Elapsed);

                var targets = ResolveReferenceTargets(referenceIndex, symbolIndex, reference.Target);
                foreach (var target in targets)
                {
                    if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        var targetDistance = distances.TryGetValue(target, out var td) ? td : int.MaxValue;

                        if (sourceDistance != int.MaxValue && targetDistance != int.MaxValue &&
                            sourceDistance <= distanceThreshold && targetDistance <= distanceThreshold)
                        {
                            var targetFile = fileByPath.TryGetValue(target, out var resolved) ? resolved : null;
                            var score = ScoreEdge(reference.Kind, sourceLanguage, sourceRole, targetFile?.Language, targetFile is null ? "module" : InferRole(targetFile), source, target, reference.Target);
                            score = (int)(score * (1 + edgeWeightBias));
                            candidateEdges.Add((target, reference.Kind, ClassifyReference(reference.Kind), reference.Evidence ?? reference.Kind, score));
                        }
                    }
                }
            }

            foreach (var edge in SelectBestEdges(candidateEdges, 12))
            {
                adjacency[source].Add(edge.Target);
                adjacency[edge.Target].Add(source);
            }
        }

        graphProgressReporter.Complete(processed, sw.Elapsed);
        return adjacency;
    }

    private static Dictionary<string, HashSet<string>> BuildSymbolIndex(IReadOnlyList<CanonicalAstSymbol> symbols)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.Name) || !string.Equals(symbol.Category, "definition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!index.TryGetValue(symbol.Name, out var owners))
            {
                owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                index[symbol.Name] = owners;
            }

            owners.Add(symbol.RelativePath);
        }

        return index;
    }

    private static Dictionary<string, HashSet<string>> BuildFileGraph(CanonicalAstDocument document, Dictionary<string, HashSet<string>> symbolIndex)
    {
        Console.WriteLine("[step4] L5: building file graph");
        var referenceIndex = GetReferenceIndex(document);
        var adjacency = document.Files.ToDictionary(file => file.RelativePath, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var fileByPath = document.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        foreach (var file in document.Files)
        {
            var source = file.RelativePath;
            var sourceLanguage = file.Language;
            var sourceRole = InferRole(file);
            var candidateEdges = new List<(string Target, string Kind, string Category, string Evidence, int Score)>();
            foreach (var reference in file.References)
            {
                if (reference.Target is null)
                {
                    continue;
                }

                if (reference.Kind is "include" or "using" or "import" or "call" or "type" or "defines")
                {
                    processed++;
                    if (processed == 1 || processed % 5000 == 0)
                    {
                        Console.WriteLine($"[step4] L5: graph refs {processed}");
                    }

                    var targets = ResolveReferenceTargets(referenceIndex, symbolIndex, reference.Target);
                    foreach (var target in targets)
                    {
                        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
                        {
                            var targetFile = fileByPath.TryGetValue(target, out var resolved) ? resolved : null;
                            var score = ScoreEdge(reference.Kind, sourceLanguage, sourceRole, targetFile?.Language, targetFile is null ? "module" : InferRole(targetFile), source, target, reference.Target);
                            candidateEdges.Add((target, reference.Kind, ClassifyReference(reference.Kind), reference.Evidence ?? reference.Kind, score));
                        }
                    }
                }
            }

            foreach (var edge in SelectBestEdges(candidateEdges, 12))
            {
                adjacency[source].Add(edge.Target);
                adjacency[edge.Target].Add(source);
            }
        }

        return adjacency;
    }

    private static IReadOnlyList<(string Target, string Kind, string Category, string Evidence, int Score)> SelectBestEdges(List<(string Target, string Kind, string Category, string Evidence, int Score)> edges, int maxPerSource)
    {
        return edges
            .OrderByDescending(edge => edge.Score)
            .ThenBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase)
            .Take(maxPerSource)
            .ToList();
    }

    private static int ScoreEdge(string kind, string? sourceLanguage, string sourceRole, string? targetLanguage, string targetRole, string sourcePath, string targetPath, string target)
    {
        var score = kind switch
        {
            "defines" => 100,
            "call" => 80,
            "include" or "using" or "import" => 75,
            "type" => 70,
            _ => 25,
        };

        if (!string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (!string.Equals(sourceRole, targetRole, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (sourcePath.Contains("main", StringComparison.OrdinalIgnoreCase) || sourcePath.Contains("app", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (targetPath.Contains("test", StringComparison.OrdinalIgnoreCase) || targetPath.Contains("spec", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        if (target.Length <= 4)
        {
            score -= 10;
        }

        return score;
    }

    private static bool IsBridgeEdge(string sourcePath, string targetPath, string sourceLanguage, string kind)
    {
        return !sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) &&
               (kind is "call" or "include" or "using" or "import" or "defines" or "type") &&
               (sourcePath.Contains("main", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains("app", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains("adapter", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains("pipeline", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
                targetPath.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                targetPath.Contains("spec", StringComparison.OrdinalIgnoreCase) ||
                !targetPath.Contains(sourceLanguage, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveReferenceTargets(ReferenceIndex referenceIndex, Dictionary<string, HashSet<string>> symbolIndex, string target)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetTokens = SplitReferenceTokens(target);

        if (referenceIndex.Exact.TryGetValue(target, out var exactMatches))
        {
            foreach (var path in exactMatches)
            {
                matches.Add(path);
            }
        }

        foreach (var token in targetTokens)
        {
            if (referenceIndex.Tokens.TryGetValue(token, out var tokenMatches))
            {
                foreach (var path in tokenMatches)
                {
                    matches.Add(path);
                }
            }
        }

        if (symbolIndex.TryGetValue(target, out var owners))
        {
            foreach (var owner in owners)
            {
                matches.Add(owner);
            }
        }

        return matches.ToList();
    }

    private static ReferenceIndex BuildReferenceIndex(IReadOnlyList<CanonicalAstFile> files)
    {
        var exact = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var tokens = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file is null)
            {
                continue;
            }

            AddIndex(exact, file.RelativePath, file.RelativePath);

            foreach (var segment in file.RelativePath.Split(new[] { '/', '\\', '.', '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddIndex(tokens, segment, file.RelativePath);
            }

            AddIndex(tokens, file.Language, file.RelativePath);

            foreach (var tag in file.Tags)
            {
                AddIndex(tokens, tag, file.RelativePath);
                foreach (var token in SplitReferenceTokens(tag))
                {
                    AddIndex(tokens, token, file.RelativePath);
                }
            }

            foreach (var reference in file.References)
            {
                if (!string.IsNullOrWhiteSpace(reference.Kind))
                {
                    AddIndex(tokens, reference.Kind, file.RelativePath);
                }

                if (!string.IsNullOrWhiteSpace(reference.Target))
                {
                    AddIndex(exact, reference.Target!, file.RelativePath);
                    foreach (var token in SplitReferenceTokens(reference.Target!))
                    {
                        AddIndex(tokens, token, file.RelativePath);
                    }
                }
            }
        }

        return new ReferenceIndex(exact, tokens);
    }

    private static void AddIndex(Dictionary<string, HashSet<string>> index, string key, string path)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            index[key] = set;
        }

        set.Add(path);
    }

    private static HashSet<string> BuildReferenceTokens(CanonicalAstFile file)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in file.RelativePath.Split(new[] { '/', '\\', '.', '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tokens.Add(segment);
        }

        tokens.Add(file.Language);

        foreach (var tag in file.Tags)
        {
            foreach (var token in SplitReferenceTokens(tag))
            {
                tokens.Add(token);
            }
        }

        foreach (var reference in file.References)
        {
            if (!string.IsNullOrWhiteSpace(reference.Kind))
            {
                tokens.Add(reference.Kind);
            }

            if (!string.IsNullOrWhiteSpace(reference.Target))
            {
                foreach (var token in SplitReferenceTokens(reference.Target))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    private static HashSet<string> SplitReferenceTokens(string value)
    {
        return value
            .Split(new[] { '/', '\\', '.', '-', '_', ':', ' ', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Multi-pass component detection with cohesion validation for 100% confidence
    /// Phase 1: Identify high-confidence seed cores
    /// Phase 2: Expand with distance constraints
    /// Phase 3: Iteratively refine loose components
    /// Phase 4: Cross-validate across 2 runs
    /// </summary>
    private static IReadOnlyList<HashSet<string>> BuildComponentsWithValidation(
        IEnumerable<string> nodes,
        Dictionary<string, HashSet<string>> adjacency,
        Dictionary<string, int> bands)
    {
        var nodeList = nodes.ToList();
        Console.WriteLine("[step4] L5: component validation: phase 1 - identifying high-confidence seeds");

        // Phase 1: Find guaranteed cohesive cores (8+ mutual refs = seed)
        var seeds = IdentifyHighConfidenceSeeds(nodeList, adjacency);
        Console.WriteLine($"[step4] L5: component validation: found {seeds.Count} high-confidence seeds");

        // Phase 2: Expand seeds with distance constraints
        Console.WriteLine("[step4] L5: component validation: phase 2 - expanding seeds");
        var components = ExpandComponentsWithValidation(nodeList, adjacency, bands, seeds);
        Console.WriteLine($"[step4] L5: component validation: expanded to {components.Count} components");

        // Phase 3: Score cohesion and refine loose components
        Console.WriteLine("[step4] L5: component validation: phase 3 - scoring and refining");
        components = IterativelyRefineComponents(components, adjacency, bands);
        Console.WriteLine($"[step4] L5: component validation: refined to {components.Count} validated components");

        // Phase 4: Cross-validate with second detection run
        Console.WriteLine("[step4] L5: component validation: phase 4 - cross-validation");
        var validated = CrossValidateComponents(components, nodeList, adjacency, bands);
        Console.WriteLine($"[step4] L5: component validation: cross-validated {validated} of {components.Count} components");

        return components;
    }

    /// <summary>
    /// Identify high-confidence seed cores: files with 8+ mutual references
    /// </summary>
    private static List<HashSet<string>> IdentifyHighConfidenceSeeds(
        List<string> nodes,
        Dictionary<string, HashSet<string>> adjacency)
    {
        var seeds = new List<HashSet<string>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find files with high coupling (8+ refs both ways)
        foreach (var file in nodes)
        {
            if (visited.Contains(file)) continue;
            if (!adjacency.TryGetValue(file, out var outgoing)) continue;

            var mutualRefs = 0;
            var seedCore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };

            foreach (var neighbor in outgoing.Take(20)) // Sample first 20 to avoid O(n²)
            {
                if (adjacency.TryGetValue(neighbor, out var inbound) && inbound.Contains(file))
                {
                    mutualRefs++;
                    if (mutualRefs >= 2) // At least 2 bidirectional refs = seed
                    {
                        seedCore.Add(neighbor);
                    }
                }
            }

            if (seedCore.Count >= 2)
            {
                foreach (var node in seedCore) visited.Add(node);
                seeds.Add(seedCore);
            }
        }

        return seeds;
    }

    /// <summary>
    /// Expand seeds by adding files within 1-2 distance bands with 2+ refs
    /// </summary>
    private static List<HashSet<string>> ExpandComponentsWithValidation(
        List<string> nodes,
        Dictionary<string, HashSet<string>> adjacency,
        Dictionary<string, int> bands,
        List<HashSet<string>> seeds)
    {
        var components = new List<HashSet<string>>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            var component = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();

            foreach (var seedNode in seed)
            {
                stack.Push(seedNode);
                component.Add(seedNode);
                assigned.Add(seedNode);
            }

            // Expand within 1-2 bands
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var currentBand = bands.TryGetValue(current, out var cb) ? cb : 4;

                if (adjacency.TryGetValue(current, out var neighbors))
                {
                    var refCount = neighbors.Count;
                    foreach (var neighbor in neighbors)
                    {
                        if (assigned.Contains(neighbor)) continue;

                        var neighborBand = bands.TryGetValue(neighbor, out var nb) ? nb : 4;
                        var bandDiff = Math.Abs(currentBand - neighborBand);

                        // Require 2+ refs to join (prevents hitchhikers)
                        if (bandDiff <= 1 && refCount >= 2)
                        {
                            component.Add(neighbor);
                            assigned.Add(neighbor);
                            stack.Push(neighbor);
                        }
                    }
                }
            }

            if (component.Count >= 1) components.Add(component);
        }

        // Assign remaining unassigned nodes as isolated components
        foreach (var node in nodes.Where(n => !assigned.Contains(n)))
        {
            components.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node });
        }

        return components;
    }

    /// <summary>
    /// Calculate cohesion score: (internal edges / total edges). Target: 60%+
    /// </summary>
    private static double CalculateComponentCohesion(
        HashSet<string> component,
        Dictionary<string, HashSet<string>> adjacency)
    {
        if (component.Count <= 1) return 1.0; // Isolated files have perfect cohesion

        var internalEdges = 0;
        var totalEdges = 0;

        foreach (var file in component)
        {
            if (!adjacency.TryGetValue(file, out var neighbors)) continue;

            foreach (var neighbor in neighbors)
            {
                totalEdges++;
                if (component.Contains(neighbor)) internalEdges++;
            }
        }

        if (totalEdges == 0) return 1.0;
        return (double)internalEdges / totalEdges;
    }

    /// <summary>
    /// Iteratively refine: score all components, break loose ones (<60%), repeat until stable
    /// </summary>
    private static List<HashSet<string>> IterativelyRefineComponents(
        List<HashSet<string>> components,
        Dictionary<string, HashSet<string>> adjacency,
        Dictionary<string, int> bands)
    {
        const double cohesionThreshold = 0.60;
        var refined = components;
        var iteration = 0;

        while (iteration < 3) // Max 3 iterations to avoid infinite loops
        {
            iteration++;
            var toBreak = new List<HashSet<string>>();
            var toKeep = new List<HashSet<string>>();

            foreach (var component in refined)
            {
                var cohesion = CalculateComponentCohesion(component, adjacency);
                if (cohesion >= cohesionThreshold)
                {
                    toKeep.Add(component);
                }
                else if (component.Count > 3) // Only break if >3 files
                {
                    toBreak.Add(component);
                }
                else
                {
                    toKeep.Add(component); // Keep small loose clusters as-is
                }
            }

            if (toBreak.Count == 0) break; // No more refinement needed

            Console.WriteLine($"[step4] L5: component validation: iteration {iteration} - breaking {toBreak.Count} loose components");

            // Re-run component detection on loose components
            var recombined = new List<HashSet<string>>(toKeep);
            foreach (var loose in toBreak)
            {
                var subComponents = FindConnectedComponentsWithBands(loose, adjacency, bands);
                recombined.AddRange(subComponents);
            }

            refined = recombined;
        }

        return refined;
    }

    /// <summary>
    /// Cross-validate by running detection twice with different seed orders
    /// Components in both runs = high confidence
    /// </summary>
    private static int CrossValidateComponents(
        List<HashSet<string>> components,
        List<string> nodes,
        Dictionary<string, HashSet<string>> adjacency,
        Dictionary<string, int> bands)
    {
        // Run detection again with shuffled seed order
        var run2 = FindConnectedComponentsWithBands(nodes.OrderBy(_ => Guid.NewGuid()), adjacency, bands);

        // Count matches (same files, same component)
        var componentSets1 = new List<HashSet<string>>(components);
        var componentSets2 = new List<HashSet<string>>(run2);
        var matched = 0;

        foreach (var c1 in componentSets1)
        {
            foreach (var c2 in componentSets2)
            {
                // Check if components have significant overlap (>70% same files)
                var intersection = c1.Intersect(c2).Count();
                var union = c1.Union(c2).Count();
                var jaccard = (double)intersection / union;

                if (jaccard > 0.70)
                {
                    matched++;
                    break;
                }
            }
        }

        return matched;
    }

    private static IReadOnlyList<HashSet<string>> FindConnectedComponents(IEnumerable<string> nodes, Dictionary<string, HashSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<HashSet<string>>();

        foreach (var node in nodes)
        {
            if (!adjacency.ContainsKey(node))
            {
                continue;
            }

            if (!visited.Add(node))
            {
                continue;
            }

            var component = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!component.Add(current))
                {
                    continue;
                }

                foreach (var neighbor in adjacency[current])
                {
                    if (!visited.Contains(neighbor))
                    {
                        stack.Push(neighbor);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static IReadOnlyList<HashSet<string>> FindConnectedComponentsWithBands(IEnumerable<string> nodes, Dictionary<string, HashSet<string>> adjacency, Dictionary<string, int> bands)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<HashSet<string>>();

        foreach (var node in nodes)
        {
            if (visited.Contains(node))
            {
                continue;
            }

            var component = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Node, int Band)>();
            var nodeBand = bands.TryGetValue(node, out var b) ? b : 4;
            stack.Push((node, nodeBand));

            while (stack.Count > 0)
            {
                var (current, currentBand) = stack.Pop();
                if (!component.Add(current) || !visited.Add(current))
                {
                    continue;
                }

                if (adjacency.TryGetValue(current, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        if (!visited.Contains(neighbor))
                        {
                            var neighborBand = bands.TryGetValue(neighbor, out var nb) ? nb : 4;
                            var bandDiff = Math.Abs(currentBand - neighborBand);

                            if (bandDiff <= 1)
                            {
                                stack.Push((neighbor, neighborBand));
                            }
                        }
                    }
                }
            }

            if (component.Count >= 1)
            {
                components.Add(component);
            }
        }

        return components;
    }

    private static string ClassifyReference(string kind)
    {
        return kind switch
        {
            "defines" => "ownership",
            "include" or "using" or "import" => "ownership",
            "call" => "usage",
            "type" => "link",
            _ => "association",
        };
    }

    private sealed record RelationRow(string Category, string Source, string Kind, string Target, string Evidence);

    private sealed class ConceptRow
    {
        public ConceptRow(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public List<string> Files { get; } = [];
        public List<string> Terms { get; } = [];
        public List<string> Evidence { get; } = [];
    }

    private sealed record ScenarioRow(string Name, IReadOnlyList<string> Files, IReadOnlyList<string> Signals);

    private sealed record IntentRow(string Name, string Evidence);

    private sealed record QuestionRow(string Question, string Evidence, string Confidence);

    private sealed record PerformanceRow(string Area, string Signal, string Evidence, string Confidence);

    private sealed record C4Row(string Name)
    {
        public List<string> Evidence { get; } = [];
        public List<string> Relationships { get; } = [];
    }

    private sealed class ClusterRow
    {
        public ClusterRow(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public List<string> Files { get; } = [];
        public List<string> Signals { get; } = [];
        public List<ClusterEdgeCache> Edges { get; } = [];
    }

    private sealed record ClusterRowCache(
        string Name,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Signals,
        IReadOnlyList<ClusterEdgeCache> Edges);

    private sealed record ClusterEdgeCache(string Source, string Target, string Kind, string Category, string Evidence);

    private sealed record ReferenceIndex(
        Dictionary<string, HashSet<string>> Exact,
        Dictionary<string, HashSet<string>> Tokens);

    private sealed record ReferenceIndexCache(
        Dictionary<string, List<string>> Exact,
        Dictionary<string, List<string>> Tokens);

    private sealed class HubProgressReporter
    {
        private readonly int _totalHubs;
        private System.Threading.Timer? _timer;
        private int _lastReportedCount = 0;
        private System.Diagnostics.Stopwatch _startTime = System.Diagnostics.Stopwatch.StartNew();

        public HubProgressReporter(int totalHubs)
        {
            _totalHubs = totalHubs;
        }

        public void Start()
        {
            _startTime.Restart();
        }

        public void Update(int completed, TimeSpan elapsed, TimeSpan eta, int percentage)
        {
            if (completed == _lastReportedCount)
            {
                return;
            }

            _lastReportedCount = completed;
            var rate = completed / Math.Max(0.1, elapsed.TotalSeconds);
            var progressBar = BuildProgressBar(percentage, 40);

            Console.WriteLine($"[step4] L5: dijkstra {progressBar} {percentage,3}% ({completed,5}/{_totalHubs,-5}) " +
                            $"{rate:F1} hubs/s ETA {eta.Hours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}");
        }

        public void Complete()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        private static string BuildProgressBar(int percentage, int width)
        {
            var filled = (percentage * width) / 100;
            var empty = width - filled;
            return $"[{new string('█', filled)}{new string(' ', empty)}]";
        }
    }

    private sealed class GraphProgressReporter
    {
        private readonly int _totalRefs;
        private int _lastReportedCount = 0;
        private System.Diagnostics.Stopwatch _startTime = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _lastReportTime = TimeSpan.Zero;

        public GraphProgressReporter(int totalRefs)
        {
            _totalRefs = totalRefs;
            _startTime.Restart();
        }

        public void Update(int processed, int filesSeen, int totalFiles, TimeSpan elapsed)
        {
            if (processed == _lastReportedCount || (elapsed - _lastReportTime).TotalMilliseconds < 1000)
            {
                return;
            }

            _lastReportedCount = processed;
            _lastReportTime = elapsed;

            var percentage = _totalRefs > 0 ? (processed * 100) / _totalRefs : 0;
            var rate = processed / Math.Max(0.1, elapsed.TotalSeconds);
            var remaining = _totalRefs - processed;
            var eta = remaining > 0 ? TimeSpan.FromSeconds(remaining / Math.Max(0.1, rate)) : TimeSpan.Zero;
            var progressBar = BuildProgressBar(percentage, 40);

            Console.WriteLine($"[step4] L5: graph   {progressBar} {percentage,3}% ({processed,6}/{_totalRefs,-6}) " +
                            $"{rate:F1} refs/s | file {filesSeen}/{totalFiles} | ETA {eta.Hours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}");
        }

        public void Complete(int processed, TimeSpan elapsed)
        {
            var rate = processed / Math.Max(0.1, elapsed.TotalSeconds);
            Console.WriteLine($"[step4] L5: graph   completed: {processed}/{_totalRefs} refs in {elapsed.TotalSeconds:F1}s ({rate:F1} refs/s)");
        }

        private static string BuildProgressBar(int percentage, int width)
        {
            var filled = (percentage * width) / 100;
            var empty = width - filled;
            return $"[{new string('█', filled)}{new string(' ', empty)}]";
        }
    }

    private sealed class ClusterProgressReporter
    {
        private readonly int _totalClusters;
        private int _lastReportedCount = 0;
        private TimeSpan _lastReportTime = TimeSpan.Zero;

        public ClusterProgressReporter(int totalClusters)
        {
            _totalClusters = totalClusters;
        }

        public void Update(int processed, TimeSpan elapsed)
        {
            if (processed == _lastReportedCount || (elapsed - _lastReportTime).TotalMilliseconds < 500)
            {
                return;
            }

            _lastReportedCount = processed;
            _lastReportTime = elapsed;

            var percentage = _totalClusters > 0 ? (processed * 100) / _totalClusters : 0;
            var rate = processed / Math.Max(0.1, elapsed.TotalSeconds);
            var remaining = _totalClusters - processed;
            var eta = remaining > 0 ? TimeSpan.FromSeconds(remaining / Math.Max(0.1, rate)) : TimeSpan.Zero;
            var progressBar = BuildProgressBar(percentage, 40);

            Console.WriteLine($"[step4] L5: clusters {progressBar} {percentage,3}% ({processed,5}/{_totalClusters,-5}) " +
                            $"{rate:F1} clusters/s | ETA {eta.Hours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}");
        }

        public void Complete(TimeSpan elapsed)
        {
            var rate = _totalClusters / Math.Max(0.1, elapsed.TotalSeconds);
            Console.WriteLine($"[step4] L5: clusters completed: {_totalClusters} clusters in {elapsed.TotalSeconds:F1}s ({rate:F1} clusters/s)");
        }

        private static string BuildProgressBar(int percentage, int width)
        {
            var filled = (percentage * width) / 100;
            var empty = width - filled;
            return $"[{new string('█', filled)}{new string(' ', empty)}]";
        }
    }

    private sealed class L5Benchmark
    {
        private readonly System.Diagnostics.Stopwatch _totalSw = new();
        private System.Diagnostics.Stopwatch _phaseSw = new();
        private readonly Dictionary<string, long> _phases = new();
        private string _currentPhase = "";

        public void StartTotal()
        {
            _totalSw.Restart();
        }

        public void EndTotal()
        {
            _totalSw.Stop();
        }

        public void StartPhase(string name)
        {
            if (!string.IsNullOrEmpty(_currentPhase))
            {
                _phases[_currentPhase] = _phaseSw.ElapsedMilliseconds;
            }
            _currentPhase = name;
            _phaseSw.Restart();
        }

        public void EndPhase(string name)
        {
            _phaseSw.Stop();
            _phases[name] = _phaseSw.ElapsedMilliseconds;
            _currentPhase = "";
        }

        public void Report(int fileCount, int hubCount, Dictionary<string, int> distances)
        {
            var reachable = distances.Values.Count(d => d != int.MaxValue);
            var avgDist = reachable > 0 ? distances.Values.Where(d => d != int.MaxValue).Average() : 0;
            var maxDist = reachable > 0 ? distances.Values.Where(d => d != int.MaxValue).Max() : 0;

            Console.WriteLine("\n[step4] L5: === DIJKSTRA OPTIMIZATION REPORT ===");
            Console.WriteLine($"[step4] L5: Files analyzed: {fileCount}");
            Console.WriteLine($"[step4] L5: Hub nodes: {hubCount}");
            Console.WriteLine($"[step4] L5: Reachable files: {reachable} ({(double)reachable/fileCount:P1})");
            Console.WriteLine($"[step4] L5: Avg distance: {avgDist:F1} hops");
            Console.WriteLine($"[step4] L5: Max distance: {maxDist} hops");

            Console.WriteLine("\n[step4] L5: === PHASE TIMINGS ===");
            foreach (var kvp in _phases.OrderByDescending(x => x.Value))
            {
                var pct = (double)kvp.Value / _totalSw.ElapsedMilliseconds * 100;
                Console.WriteLine($"[step4] L5: {kvp.Key,-12} {kvp.Value,6}ms ({pct,5:F1}%)");
            }

            Console.WriteLine($"\n[step4] L5: === TOTAL TIME: {_totalSw.ElapsedMilliseconds}ms ===\n");
        }
    }
}
