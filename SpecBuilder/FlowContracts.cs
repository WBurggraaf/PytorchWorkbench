namespace SpecBuilder;

internal interface IPipelineFlow
{
    string Name { get; }
    string Description { get; }
    Task<FlowResult> ExecuteAsync();
}

internal sealed record FlowResult(string Message, string? OutputPath = null);
