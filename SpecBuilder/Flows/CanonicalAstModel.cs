using System.Text.Json.Serialization;

namespace SpecBuilder.Flows;

internal sealed record CanonicalAstDocument(
    string WorkspaceRoot,
    string OriginRoot,
    DateTimeOffset GeneratedAt,
    string SnapshotPath,
    IReadOnlyList<CanonicalAstFile> Files,
    IReadOnlyList<CanonicalAstGroup> Groups,
    IReadOnlyList<CanonicalAstSymbol> Symbols);

internal sealed record CanonicalAstGroup(
    string Key,
    int FileCount,
    int TotalRootNodes,
    IReadOnlyList<string> Extensions);

internal sealed record CanonicalAstFile(
    string RelativePath,
    string Extension,
    string Language,
    string Status,
    int RootNodeCount,
    CanonicalAstNode Root,
    IReadOnlyList<string> Tags,
    IReadOnlyList<CanonicalAstReference> References);

internal sealed record CanonicalAstReference(
    string Kind,
    string? Target,
    string? TargetPath,
    string? Evidence);

internal sealed record CanonicalAstNode(
    string Kind,
    string? Name,
    int? Line,
    string? Signature,
    string? Summary,
    IReadOnlyList<CanonicalAstNode> Children,
    IReadOnlyList<CanonicalAstReference> References);

internal sealed record CanonicalAstSymbol(
    string Name,
    string Kind,
    string Category,
    string RelativePath,
    string Language,
    string? Evidence);

internal static class CanonicalAstConverter
{
    public static CanonicalAstDocument Convert(
        string workspaceRoot,
        string originRoot,
        string snapshotPath,
        DateTimeOffset generatedAt,
        IReadOnlyList<OllamaExtensionAnalysisFlow.AstFileRecord> files,
        IReadOnlyList<OllamaExtensionAnalysisFlow.AstGroupRecord> groups)
    {
        var canonicalFiles = files.Select(ConvertFile).ToList();
        var canonicalGroups = groups.Select(group => new CanonicalAstGroup(
            group.Key,
            group.FileCount,
            group.TotalRootNodes,
            group.Extensions.ToList())).ToList();
        var canonicalSymbols = BuildSymbols(files).ToList();

        return new CanonicalAstDocument(workspaceRoot, originRoot, generatedAt, snapshotPath, canonicalFiles, canonicalGroups, canonicalSymbols);
    }

    private static CanonicalAstFile ConvertFile(OllamaExtensionAnalysisFlow.AstFileRecord file)
    {
        return new CanonicalAstFile(
            file.RelativePath,
            file.Extension,
            file.Language,
            file.Status,
            file.RootNodeCount,
            ConvertNode(file.Root),
            file.Tags.ToList(),
            file.References.Select(ConvertReference).ToList());
    }

    private static CanonicalAstNode ConvertNode(OllamaExtensionAnalysisFlow.AstNode node)
    {
        return new CanonicalAstNode(
            node.Kind,
            node.Name,
            node.Line <= 0 ? null : node.Line,
            node.Signature,
            node.Summary,
            node.Children.Select(ConvertNode).ToList(),
            node.References.Select(ConvertReference).ToList());
    }

    private static CanonicalAstReference ConvertReference(OllamaExtensionAnalysisFlow.AstReference reference)
    {
        return new CanonicalAstReference(reference.Kind, reference.Target, reference.TargetPath, reference.Evidence);
    }

    private static IEnumerable<CanonicalAstSymbol> BuildSymbols(IReadOnlyList<OllamaExtensionAnalysisFlow.AstFileRecord> files)
    {
        foreach (var file in files)
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
}
