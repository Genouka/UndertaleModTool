using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UndertaleModMcp.Resources;
using UndertaleModMcp.Services;
using UndertaleModMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<GameDataSession>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DataFileTools>()
    .WithTools<CodeTools>()
    .WithTools<ResourceTools>()
    .WithTools<SearchTools>()
    .WithResources<GameDataResources>();

await builder.Build().RunAsync();
