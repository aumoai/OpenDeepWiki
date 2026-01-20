using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoalaWiki.Domains;
using KoalaWiki.Domains.DocumentFile;
using KoalaWiki.Domains.Warehouse;
using KoalaWiki.KoalaWarehouse.DocumentPending;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Newtonsoft.Json;
using Polly;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace KoalaWiki.BackendService;

public partial class WarehouseProcessingTask
{
    public async Task<string> HandleAnalyseAsync(Warehouse warehouse, Document? document, IKoalaWikiContext dbContext)
    {
        try
        {
            logger.LogInformation("Step 1: Starting warehouse update {GitPath}", document.GitPath);

            // 1. Update warehouse
            var (commits, commitId) = GitService.PullRepository(document.GitPath, warehouse.Version,
                warehouse.Branch, warehouse.GitUserName, warehouse.GitPassword);

            logger.LogInformation("Warehouse update completed, retrieved {CommitCount} commit records", commits?.Count ?? 0);
            if (commits == null || commits.Count == 0)
            {
                logger.LogInformation("No new commit records");
                return string.Empty;
            }

            // Get update content and updated files
            var commitPrompt = new StringBuilder();
            if (commits is { Count: > 0 })
            {
                using var repo = new Repository(document.GitPath);

                foreach (var commitItem in commits.Select(commit => repo.Lookup<Commit>(commit.Sha)))
                {
                    commitPrompt.AppendLine($"<commit>\n{commitItem.Message}");
                    // Get list of currently updated files
                    if (commitItem.Parents.Any())
                    {
                        var parent = commitItem.Parents.First();
                        var comparison = repo.Diff.Compare<TreeChanges>(parent.Tree, commitItem.Tree);

                        foreach (var change in comparison)
                        {
                            commitPrompt.AppendLine($" - {change.Status}: {change.Path}");
                        }
                    }

                    commitPrompt.AppendLine("</commit>");
                }
            }
            else
            {
                logger.LogInformation("No new commit records");

                // If there are no new commit records, return directly
                return string.Empty;
            }


            logger.LogInformation("Step 2: Getting document catalog");
            var catalogues = await dbContext.DocumentCatalogs
                .AsNoTracking()
                .Where(x => x.WarehouseId == warehouse.Id)
                .ToListAsync();
            logger.LogInformation("Retrieved {CatalogCount} catalog items", catalogues.Count);

            logger.LogInformation("Step 3: Creating kernel and preparing analysis");

            // First get the tree structure
            var kernel = await KernelFactory.GetKernel(OpenAIOptions.Endpoint,
                OpenAIOptions.ChatApiKey, document.GitPath, OpenAIOptions.ChatModel, false);

            var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();

            var prompt = Prompt.AnalyzeNewCatalogue
                .Replace("{{git_repository}}", warehouse.Address.Replace(".git", ""))
                .Replace("{{document_catalogue}}", JsonSerializer.Serialize(catalogues, JsonSerializerOptions.Web))
                .Replace("{{git_commit}}", commitPrompt.ToString())
                .Replace("{{catalogue}}", document.GetCatalogueSmartFilterOptimized());

            history.AddUserMessage(prompt);

            logger.LogInformation("Step 4: Starting AI analysis");

            var st = new StringBuilder();

            // Use Polly to create retry policy
            var retryPolicy = Policy
                .Handle<Exception>() // Handle all exceptions
                .WaitAndRetryAsync(
                    retryCount: 3, // Maximum retry count
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), // Exponential backoff strategy
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        logger.LogWarning("Retry analysis attempt {RetryCount}", retryCount);
                        logger.LogError(exception, "AI analysis failed (attempt {RetryCount}/3): {ErrorMessage}",
                            retryCount, exception.Message);
                        logger.LogInformation("Waiting {Delay} seconds before retry", timeSpan.TotalSeconds);
                    }
                );

            // Execute async operation with retry policy
            var result = await retryPolicy.ExecuteAsync(async () =>
            {
                st.Clear();

                await foreach (var item in chatCompletion.GetStreamingChatMessageContentsAsync(history,
                                   new OpenAIPromptExecutionSettings()
                                   {
                                       MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.AnalysisModel),
                                       Temperature = 0.3,
                                   }, kernel))
                {
                    if (!string.IsNullOrEmpty(item.Content))
                    {
                        st.Append(item.Content);
                    }
                }

                logger.LogInformation("AI analysis completed successfully");

                // Extract <document_structure></document_structure> using regex
                var regex = new Regex(@"<document_structure>(.*?)</document_structure>", RegexOptions.Singleline);
                var match = regex.Match(st.ToString());
                if (match.Success)
                {
                    st.Clear();
                    st.Append(match.Groups[1].Value);
                }

                // Extract ```json using regex
                var jsonRegex = new Regex(@"```json(.*?)```", RegexOptions.Singleline);
                var jsonMatch = jsonRegex.Match(st.ToString());
                if (jsonMatch.Success)
                {
                    st.Clear();
                    st.Append(jsonMatch.Groups[1].Value);
                }

                // Parse
                var result = JsonConvert.DeserializeObject<WareHouseCatalogue>(st.ToString());

                return result;
            });

            logger.LogInformation("Step 5: Processing analysis results, length: {ResultLength} characters", st.Length);

            // Can continue processing analysis results here

            await dbContext.DocumentCatalogs.Where(x => result.delete_id.Contains(x.Id))
                .ExecuteUpdateAsync(x =>
                    x.SetProperty(a => a.IsDeleted, true)
                        .SetProperty(a => a.DeletedTime, DateTime.Now));


            var documents = new List<(WareHouseCatalogueType, DocumentCatalog)>();
            ProcessCatalogueItems(result.items.ToList(), null, warehouse, document, documents);

            logger.LogInformation("Step 6: Updating document catalog");

            foreach (var tuple in documents)
            {
                if (tuple.Item1 == WareHouseCatalogueType.Update)
                {
                    // Need to delete existing first
                    await dbContext.DocumentCatalogs
                        .Where(x => x.WarehouseId == warehouse.Id && x.Id == tuple.Item2.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(a => a.IsDeleted, true)
                            .SetProperty(a => a.DeletedTime, DateTime.UtcNow));
                }
            }

            var fileKernel =await  KernelFactory.GetKernel(OpenAIOptions.Endpoint, OpenAIOptions.ChatApiKey, document.GitPath,
                OpenAIOptions.ChatModel, false);

            foreach (var valueTuple in documents)
            {
                if (valueTuple.Item2.Id == null)
                {
                    valueTuple.Item2.Id = Guid.NewGuid().ToString("N") + valueTuple.Item2.Url;
                }
            }

            await DocumentPendingService.HandlePendingDocumentsAsync(documents.Select(x => x.Item2).ToList(),
                fileKernel,
                document.GetCatalogueSmartFilterOptimized(),
                warehouse.Address, warehouse, document.GitPath, dbContext, warehouse.Classify);

            logger.LogInformation("Warehouse {WarehouseName} analysis completed", warehouse.Name);

            var readme = await DocumentsHelper.ReadMeFile(document.GitPath);

            var commitResult = await GenerateUpdateLogAsync(document.GitPath, warehouse,
                readme, warehouse.Address, warehouse.Branch, dbContext);

            await dbContext.DocumentCommitRecords.AddRangeAsync(commitResult.Select(x => new DocumentCommitRecord()
            {
                WarehouseId = warehouse.Id,
                CreatedAt = DateTime.Now,
                Author = string.Empty,
                Id = Guid.NewGuid().ToString("N"),
                CommitMessage = x.description,
                Title = x.title,
                LastUpdate = x.date,
            }));

            await dbContext.SaveChangesAsync();


            return commitId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception occurred during warehouse analysis: {ErrorMessage}", ex.Message);
            throw;
        }
    }


    private static void ProcessCatalogueItems(List<WareHouseCatalogueItems> items, string? parentId,
        Warehouse warehouse,
        Document document, List<(WareHouseCatalogueType, DocumentCatalog)>? documents)
    {
        int order = 0; // Create sorting counter
        foreach (var item in items)
        {
            item.title = item.title.Replace(" ", "");
            var documentItem = new DocumentCatalog
            {
                WarehouseId = warehouse.Id,
                Description = item.title,
                Id = item.Id,
                Name = item.name,
                Url = item.title,
                DucumentId = document.Id,
                ParentId = parentId,
                Prompt = item.prompt,
                Order = order++ // Set order value for each item at current level and increment
            };
            if (item.type == "update")
            {
                documents.Add((WareHouseCatalogueType.Update, documentItem));
            }
            else
            {
                documents.Add((WareHouseCatalogueType.Add, documentItem));
            }

            if (item.children?.Length > 0)
            {
                ProcessCatalogueItems(item.children.ToList(), documentItem.Id, warehouse, document,
                    documents);
            }
        }
    }
}

public class WareHouseCatalogue
{
    public string[] delete_id { get; set; }

    public WareHouseCatalogueItems[] items { get; set; }
}

public class WareHouseCatalogueItems
{
    public string Id { get; set; }

    public string title { get; set; }

    public string name { get; set; }

    public string type { get; set; }

    public string prompt { get; set; }

    public WareHouseCatalogueItems[]? children { get; set; }
}

public enum WareHouseCatalogueType
{
    Update,
    Add
}