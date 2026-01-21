using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using KoalaWiki.Domains.MCP;
using KoalaWiki.Tools;
using ModelContextProtocol.Server;

namespace KoalaWiki.MCP.Tools;

public class McpAgentTool
{
    /// <summary>
    /// Answer questions about the repository based on its documentation and code
    /// </summary>
    /// <param name="server"></param>
    /// <param name="question"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    [McpServerTool(Name = "ask_repository")]
    public static async Task<string> AskRepositoryAsync(
        McpServer server,
        string question)
    {
        await using var scope = server.Services.CreateAsyncScope();

        var koala = scope.ServiceProvider.GetRequiredService<IKoalaWikiContext>();

        // Read from nested koalawiki JsonObject in experimental capabilities
        var koalawiki = (JsonObject)server.ServerOptions.Capabilities!.Experimental!["koalawiki"];
        var name = koalawiki["name"]!.GetValue<string>();
        var owner = koalawiki["owner"]!.GetValue<string>();

        var warehouse = await koala.Warehouses
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationName == owner && x.Name == name);

        if (warehouse == null)
        {
            throw new Exception($"抱歉，您的仓库 {owner}/{name} 不存在或已被删除。");
        }

        var document = await koala.Documents
            .AsNoTracking()
            .Where(x => x.WarehouseId == warehouse.Id)
            .FirstOrDefaultAsync();

        if (document == null)
        {
            throw new Exception("抱歉，您的仓库没有文档，请先生成仓库文档。");
        }


        var kernel =await  KernelFactory.GetKernel(OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey, document.GitPath, OpenAIOptions.DeepResearchModel, false);

        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // 解析仓库的目录结构
        var path = document.GitPath;

        var complete = string.Empty;

        var token = new CancellationTokenSource();

        var fileKernel = await KernelFactory.GetKernel(OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey, path, OpenAIOptions.DeepResearchModel, false, kernelBuilderAction: (builder =>
            {
                builder.Plugins.AddFromObject(new CompleteTool((async value =>
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        complete = value;

                        await token.CancelAsync().ConfigureAwait(false);
                    }
                })));
                
                builder.Plugins.AddFromObject(new DocumentationTool(warehouse.Id, koala), "docs");
            }));

        var history = new ChatHistory();
        history.AddSystemEnhance();

        var catalogue = document.GetCatalogueSmartFilterOptimized();

        history.AddUserMessage(await PromptContext.Chat(nameof(PromptConstant.Chat.Responses),
            new KernelArguments()
            {
                ["catalogue"] = catalogue,
                ["repository_name"] = warehouse.Name,
            }, OpenAIOptions.ChatModel));

        history.AddUserMessage([
            new TextContent(question),
            new TextContent("""
                            <system-reminder>
                            Note:
                            - What the user needs is a detailed and professional response based on the contents of the aforementioned warehouse.
                            - Answer the user's questions as detailedly and promptly as possible.
                            </system-reminder>
                            """)
        ]);

        var first = true;

        DocumentContext.DocumentStore = new DocumentStore();

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();

        try
        {
            await foreach (var chatItem in chat.GetStreamingChatMessageContentsAsync(history,
                               new OpenAIPromptExecutionSettings()
                               {
                                   ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                                   MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel)
                               }, fileKernel, token.Token))
            {
                token.Token.ThrowIfCancellationRequested();

                if (chatItem.InnerContent is StreamingChatCompletionUpdate message)
                {
                    if (string.IsNullOrEmpty(chatItem.Content))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(chatItem.Content))
                    {
                        sb.Append(chatItem.Content);
                    }
                }
            }

            sw.Stop();

            if (!string.IsNullOrEmpty(complete))
            {
                sb.Clear();
                sb.Append(complete);
            }

            if (string.IsNullOrWhiteSpace(sb.ToString()))
            {
                var contextBuilder = new StringBuilder();
                foreach (var message in history)
                {
                    if (message.Role == AuthorRole.System)
                        continue;
                    
                    if (message.Items != null)
                    {
                        foreach (var item in message.Items)
                        {
                            if (item is FunctionResultContent functionResult)
                            {
                                contextBuilder.AppendLine($"<file name=\"{functionResult.FunctionName}\">");
                                contextBuilder.AppendLine(functionResult.Result?.ToString() ?? string.Empty);
                                contextBuilder.AppendLine("</file>");
                                contextBuilder.AppendLine();
                            }
                        }
                    }
                }

                var cleanHistory = new ChatHistory();
                cleanHistory.AddUserMessage($"""
                    You are an AI assistant specialized in software engineering and code analysis.
                    
                    Based on the following files from the repository, answer the user's question comprehensively.

                    <files>
                    {contextBuilder}
                    </files>

                    <question>{question}</question>

                    Provide a detailed, professional answer based on the file contents above.
                    """);

                var noToolsKernel = Kernel.CreateBuilder()
                    .AddOpenAIChatCompletion(OpenAIOptions.DeepResearchModel, new Uri(OpenAIOptions.Endpoint), OpenAIOptions.ChatApiKey)
                    .Build();

                await foreach (var chatItem in chat.GetStreamingChatMessageContentsAsync(cleanHistory,
                                   new OpenAIPromptExecutionSettings()
                                   {
                                       MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel)
                                   }, noToolsKernel))
                {
                    if (!string.IsNullOrEmpty(chatItem.Content))
                    {
                        sb.Append(chatItem.Content);
                    }
                }
            }

            var mcpHistory = new MCPHistory()
            {
                Id = Guid.NewGuid().ToString(),
                CostTime = (int)sw.ElapsedMilliseconds,
                CreatedAt = DateTime.Now,
                Question = question,
                Answer = sb.ToString(),
                WarehouseId = warehouse.Id,
                UserAgent = string.Empty,
                Ip = string.Empty,
                UserId = string.Empty
            };

            await koala.MCPHistories.AddAsync(mcpHistory);
            await koala.SaveChangesAsync();

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            return complete;
        }
        catch (Exception e)
        {
            return "抱歉，发生了错误: " + e.Message;
        }
    }
}