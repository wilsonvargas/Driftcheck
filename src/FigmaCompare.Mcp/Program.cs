using FigmaCompare.Mcp;
using FigmaCompare.Mcp.Android;
using FigmaCompare.Mcp.Comparison;
using FigmaCompare.Mcp.Figma;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var builder = Host.CreateApplicationBuilder(args);

// stdout está reservado para el protocolo MCP; todo el log va a stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton<Settings>();
builder.Services.AddHttpClient<FigmaClient>();
builder.Services.AddSingleton<AdbClient>();
builder.Services.AddSingleton<ScreenComparer>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
    {
        // El SDK solo pasa al cliente el mensaje de un McpException; el resto llega como
        // "An error occurred invoking...". Nuestras excepciones traen mensajes útiles para el agente.
        try
        {
            return await next(context, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            throw new McpException(ex.Message, ex);
        }
    }));

await builder.Build().RunAsync();
