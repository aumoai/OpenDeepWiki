using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;
using KoalaWiki.MCP.Tools;
using KoalaWiki.Mem0;
using KoalaWiki.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KoalaWiki.MCP;

public static class McpExtensions
{
    public static IServiceCollection AddKoalaMcp(this IServiceCollection service)
    {
        service.AddScoped<McpAgentTool>();

        service.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.ConfigureSessionOptions += async (context, serverOptions, token) =>
                {
                    var owner = context.Request.Query["owner"].ToString().ToLower();
                    var name = context.Request.Query["name"].ToString().ToLower();
                    var dbContext = context.RequestServices!.GetService<IKoalaWikiContext>();

                    var warehouse = await dbContext!.Warehouses
                        .Where(x => x.OrganizationName.ToLower() == owner && x.Name.ToLower() == name)
                        .FirstOrDefaultAsync(token);

                    if (warehouse == null)
                    {
                        throw new Exception($"Warehouse {owner}/{name} not found.");
                    }

                    serverOptions.InitializationTimeout = TimeSpan.FromSeconds(600);
                    serverOptions.Capabilities = new ServerCapabilities
                    {
                        // Store warehouse info in Experimental using JsonObject for proper serialization
                        // MCP spec requires experimental values to be objects, not primitives
                        Experimental = new Dictionary<string, object>
                        {
                            ["koalawiki"] = new JsonObject
                            {
                                ["owner"] = owner,
                                ["name"] = name,
                                ["warehouseId"] = warehouse.Id
                            }
                        }
                    };


                    serverOptions.Capabilities.Tools = new();
                    var toolCollection = serverOptions.ToolCollection = [];

                    var warehouseTool = GetToolsForType<McpAgentTool>(owner, name);
                    foreach (var tool in warehouseTool)
                    {
                        toolCollection.Add(tool);
                    }

                    if (OpenAIOptions.EnableMem0)
                    {
                        var mem0 = GetToolsForType<RagTool>(owner, name);
                        foreach (var tool in mem0)
                        {
                            toolCollection.Add(tool);
                        }
                    }

                    await Task.CompletedTask;
                };
            });

        return service;
    }

    static McpServerTool[] GetToolsForType<[DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods)]
        T>(string owner, string name)
    {
        var tools = new List<McpServerTool>();
        var toolType = typeof(T);
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Any());

        foreach (var method in methods)
        {
            try
            {
                if (method.Name == "AskRepositoryAsync")
                {
                    var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions()
                    {
                        Description =
                            $"Ask questions about the {owner}/{name} repository and get detailed answers based on its code and documentation. Use this to understand how the project works, find specific implementations, learn about APIs, troubleshoot issues, or get explanations about any aspect of the codebase."
                    });
                    tools.Add(tool);
                }
                else
                {
                    var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions()
                    {
                        Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    });
                    tools.Add(tool);
                }
            }
            catch (Exception ex)
            {
                // Log error but continue with other tools
                Console.WriteLine($"Failed to add tool {toolType.Name}.{method.Name}: {ex.Message}");
            }
        }

        return [.. tools];
    }
}