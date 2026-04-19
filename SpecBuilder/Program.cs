using SpecBuilder;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var app = new PipelineHost(projectRoot);
await app.RunAsync();
