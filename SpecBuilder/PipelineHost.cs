using SpecBuilder.Flows;

namespace SpecBuilder;

internal sealed class PipelineHost
{
    private readonly IReadOnlyList<IPipelineFlow> _flows;
    private readonly string _appRoot;

    public PipelineHost(string appRoot)
    {
        _appRoot = Path.GetFullPath(Path.Combine(appRoot, ".."));
        _flows = new IPipelineFlow[]
        {
            new CodeInventoryFlow(_appRoot),
            new OllamaLanguagesFlow(_appRoot),
            new OllamaExtensionAnalysisFlow(_appRoot),
        };
    }

    public async Task RunAsync()
    {
        while (true)
        {
            TryClearConsole();
            Console.WriteLine("SpecBuilder");
            Console.WriteLine("-----------");
            Console.WriteLine($"Workspace: {_appRoot}");
            Console.WriteLine();

            for (var i = 0; i < _flows.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {_flows[i].Name}");
            }

            Console.WriteLine("Q. Quit");
            Console.WriteLine();
            Console.Write("Select a step: ");

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                continue;
            }

            if (input.Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (int.TryParse(input, out var selected) &&
                selected >= 1 &&
                selected <= _flows.Count)
            {
                await RunFlowAsync(_flows[selected - 1]);
                continue;
            }

            Console.WriteLine("Invalid selection. Press Enter to continue.");
            Console.ReadLine();
        }
    }

    private static void TryClearConsole()
    {
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }
    }

    private static async Task RunFlowAsync(IPipelineFlow flow)
    {
        TryClearConsole();
        Console.WriteLine(flow.Name);
        Console.WriteLine(new string('-', flow.Name.Length));
        Console.WriteLine(flow.Description);
        Console.WriteLine();

        var result = await flow.ExecuteAsync();
        Console.WriteLine(result.Message);
        if (!string.IsNullOrWhiteSpace(result.OutputPath))
        {
            Console.WriteLine($"Output: {result.OutputPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Press Enter to return to the menu.");
        Console.ReadLine();
    }
}
