using System.Text.RegularExpressions;
using KoalaWiki.Domains.Warehouse;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace KoalaWiki.BackendService;

public partial class WarehouseProcessingTask
{
    /// <summary>
    /// Generate update log
    /// </summary>
    public async Task<List<CommitResultDto>> GenerateUpdateLogAsync(string gitPath,
        Warehouse warehouse,
        string readme, string gitRepositoryUrl, string branch, IKoalaWikiContext koalaWikiContext)
    {
        // Get warehouse last update time
        var records = await koalaWikiContext.DocumentCommitRecords
            .Where(x => x.WarehouseId == warehouse.Id)
            // Get most recent record LastUpdate
            .OrderByDescending(x => x.LastUpdate).FirstOrDefaultAsync();

        // Read git log
        using var repo = new Repository(gitPath, new RepositoryOptions());

        // Commits with time greater than records
        var log = repo.Commits
            .Where(x => records == null || x.Committer.When > records?.LastUpdate)
            .OrderByDescending(x => x.Committer.When)
            .ThenBy(x => x.Committer.When)
            .ToList();

        var kernel = await KernelFactory.GetKernel(OpenAIOptions.Endpoint, OpenAIOptions.ChatApiKey, gitPath,
            OpenAIOptions.ChatModel);

        string commitMessage = string.Empty;
        foreach (var commit in log)
        {
            commitMessage += "Committer: " + commit.Committer.Name + "\nCommit content\n<message>\n" + commit.Message +
                             "</message>";

            commitMessage += "\nCommit time: " + commit.Committer.When.ToString("yyyy-MM-dd HH:mm:ss") + "\n";
        }

        var plugin = kernel.Plugins["CodeAnalysis"]["CommitAnalyze"];

        var str = string.Empty;
        await foreach (var item in kernel.InvokeStreamingAsync(plugin, new KernelArguments()
                       {
                           ["readme"] = readme,
                           ["git_repository"] = gitRepositoryUrl,
                           ["commit_message"] = commitMessage,
                           ["git_branch"] = branch
                       }))
        {
            str += item;
        }

        var regex = new Regex(@"<changelog>(.*?)</changelog>",
            RegexOptions.Singleline);

        var match = regex.Match(str);

        if (match.Success)
        {
            // Extracted content
            str = match.Groups[1].Value;
        }

        var result = JsonConvert.DeserializeObject<List<CommitResultDto>>(str);

        return result;
    }

    public class CommitResultDto
    {
        public DateTime date { get; set; }

        public string title { get; set; }

        public string description { get; set; }
    }
}