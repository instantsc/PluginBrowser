using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Model;
using Octokit;

public class Methods
{
    public static async Task GenerateNewData()
    {
        await using var inputStream = File.Exists("input.txt") ? File.OpenRead("input.txt") : Console.OpenStandardInput();
        await using var outputStream = Console.OpenStandardOutput();
        var inputModel = JsonSerializer.Deserialize<InputModel>(inputStream);
        var secret = File.Exists("key.txt")
            ? File.ReadAllText("key.txt")
            : Environment.GetEnvironmentVariable("gh_token") ?? throw new Exception("Auth token not found");
        var githubApi = new GitHubClient(new ProductHeaderValue("test-plugin-browser"))
            { Credentials = new Credentials(secret) };
        var plugins = new List<PluginDescription>();
        foreach (var plugin in inputModel.Plugins)
        {
            try
            {
                var forks = new EquatableList<PluginForkDescription>();
                foreach (var pluginRepository in plugin.Repositories)
                {
                    var tries = 5;
                    while (tries-- > 0)
                    {
                        try
                        {
                            var repositoryLocation = pluginRepository.Location ?? pluginRepository.Author;
                            var repo = await githubApi.Repository.Get(repositoryLocation, pluginRepository.Name);
                            var releases = await githubApi.Repository.Release.GetAll(repo.Id, new ApiOptions { PageCount = 1 });
                            var releaseDescriptions = releases
                                .Where(x => x.PublishedAt != null)
                                .Select(release => new ReleaseDescription(release.TagName, release.Name, release.Assets.Select(a => a.Name).ToEquatableList(), release.Body,
                                    release.CreatedAt.UtcDateTime))
                                .ToEquatableList();

                            var branch = await githubApi.Repository.Branch.Get(repo.Id, pluginRepository.Branch ?? repo.DefaultBranch);
                            var commit = await githubApi.Repository.Commit.Get(repo.Id, branch.Commit.Sha);
                            var commitDescription = new CommitDescription(commit.Commit.Message, commit.Commit.Sha, commit.Author?.Login ?? commit.Commit.Author.Name,
                                commit.Commit.Committer.Date.UtcDateTime);
                            forks.Add(new PluginForkDescription(pluginRepository.Author, repositoryLocation, pluginRepository.Name, commitDescription, releaseDescriptions));
                            break;
                        }
                        catch (NotFoundException ex)
                        {
                            Console.Error.WriteLine($"Fork {pluginRepository} not found: {ex}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Unable to process fork {pluginRepository}: {ex}");
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                    }
                }

                plugins.Add(new PluginDescription(plugin.Name, plugin.OriginalAuthor, forks, plugin.Description, plugin.EndorsedAuthor));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unable to process repository {plugin}: {ex}");
            }
        }

        var browserModel = new BrowserModel(plugins, DateTime.UtcNow, Constants.ExpectedModelVersion);
        JsonSerializer.Serialize(outputStream, browserModel);
    }

    public static async Task PostUpdateNotes(FileInfo newFilePath, FileInfo oldFilePath, Uri releaseWebHook)
    {
        var now = DateTime.UtcNow;
        List<PluginForkDescription> oldContent;
        BrowserModel? browserModel;
        await using (var stream1 = newFilePath.OpenRead())
            browserModel = JsonSerializer.Deserialize<BrowserModel>(stream1);
        if (browserModel?.ModelVersion != Constants.ExpectedModelVersion)
        {
            return;
        }

        var newContent = browserModel.PluginDescriptions.SelectMany(p => p.Forks.Select(f => f)).ToList();
        await using (var stream2 = oldFilePath.OpenRead())
        {
            var oldContentModel = JsonSerializer.Deserialize<BrowserModel>(stream2);
            if (oldContentModel?.ModelVersion != Constants.ExpectedModelVersion)
            {
                return;
            }

            oldContent = oldContentModel.PluginDescriptions.SelectMany(p => p.Forks.Select(f => f)).ToList();
        }

        var forkToPluginMap = browserModel.PluginDescriptions.SelectMany(p => p.Forks.Select(f => (p, f))).ToDictionary(x => x.f, x => x.p);
        var changedPlugins = newContent.Join(oldContent, x => (x.Author, x.Name), x => (x.Author, x.Name), (a, b) => (a, b)).Where(x => !x.a.Equals(x.b)).ToList();
        var newCommits = changedPlugins.Where(x => !x.a.LatestCommit.Equals(x.b.LatestCommit)).Select(x => x.a).ToList();
        var newReleases = changedPlugins.Where(x => x.a.LatestRelease?.Equals(x.b.LatestRelease) != true)
            .Select(x => (x.a, NewReleases: x.a.Releases.ExceptBy(x.b.Releases.Select(r => r.Id), r => r.Id)
                .Where(release => now - release.Date < TimeSpan.FromDays(30))
                .ToList()))
            .Where(x => x.NewReleases.Any())
            .ToList();
        var changedPluginDetails = newCommits
            .OuterJoinUnique(newReleases.AsNullable(), x => x, x => x?.a!)
            .Select(x => (Fork: x.Item1 ?? x.Item2?.a!, x.Item1?.LatestCommit, x.Item2?.NewReleases))
            .ToList();
        var newPlugins = newContent.ExceptBy(oldContent.Select(x => (x.Author, x.Name)), x => (x.Author, x.Name)).ToList();
        var sb = new StringBuilder();
        if (!changedPluginDetails.Any() && !newPlugins.Any())
        {
            Console.WriteLine("No updates, exiting");
            return;
        }

        var payLoad = new WebhookPayload
        {
            EmbeddedMessages = []
        };

        foreach (var (key, changed, @new) in changedPluginDetails
                     .GroupBy(x => forkToPluginMap[x.Fork])
                     .OuterJoinUnique(newPlugins.GroupBy(x => forkToPluginMap[x]), x => x.Key, x => x.Key)
                     .Select(x => (x.Item1?.Key ?? x.Item2?.Key!, x.Item1, x.Item2))
                     .OrderBy(x => x.Item1.Name))
        {

            if (@new != null)
            {
                foreach (var fork in @new.OrderBy(x => x.Author).ThenBy(x => x.Name))
                {
                    var processedCommitMessage = SubstringBefore(fork.LatestCommit.Message, new[] { '\r', '\n' }).Replace("`", "");
                    var pluginForkEmbed = new EmbedMessages()
                    {
                        Author = $"Author: {fork.Author}",
                        AuthorURL = $"https://github.com/{fork.Author}",
                        IsNewFork = true,
                        Fork = new EmbedField { IsInline = true, FieldName = "__New__ Fork", FieldDesc = $"[{fork.Author}/{fork.Name}](<{GithubUrls.Repository(fork.Location, fork.Name)}>)" },
                        CommitWhen = new EmbedField { IsInline = true, FieldName = "Last Commit Date", FieldDesc = $"{ToUnixTimestampTag(fork.LatestCommit.Date)}" },
                        LastCommitMessage = new EmbedField { IsInline = false, FieldName = "Last Commit Message", FieldDesc = $"```\n{processedCommitMessage}\n```" }
                    };

                    var latestRelease = fork.LatestRelease;
                    if (latestRelease != null)
                    {
                        var displayTitle = latestRelease.Title.Replace("`", "");

                        pluginForkEmbed.HasRelease = true;
                        pluginForkEmbed.NewLatestRelease = new EmbedField { IsInline = true, FieldName = "New Latest Release", FieldDesc = $"```\n{displayTitle}\n```" };
                        pluginForkEmbed.NewLatestReleaseTag = new EmbedField
                        { IsInline = true, FieldName = "Release Tag", FieldDesc = $"[{latestRelease.Id}](<{GithubUrls.Release(fork.Author, fork.Name, latestRelease.Id)}>)" };
                    }

                    payLoad.EmbeddedMessages.Add(pluginForkEmbed);
                }
            }

            if (changed != null)
            {
                foreach (var (fork, newCommit, newForkReleases) in changed.OrderBy(x => x.Fork.Author).ThenBy(x => x.Fork.Name))
                {
                    var pluginForkEmbed = new EmbedMessages
                    {
                        Author = $"Author: {fork.Author}",
                        AuthorURL = $"https://github.com/{fork.Author}",
                        IsNewFork = false,
                        Fork = new EmbedField { IsInline = true, FieldName = "Fork", FieldDesc = $"[{fork.Author}/{fork.Name}](<{GithubUrls.Repository(fork.Location, fork.Name)}>)" }
                    };

                    if (newCommit != null)
                    {
                        var processedCommitMessage = SubstringBefore(newCommit.Message, new[] { '\r', '\n' }).Replace("`", "");

                        pluginForkEmbed.CommitWhen = new EmbedField { IsInline = true, FieldName = "Last Commit Date", FieldDesc = $"{ToUnixTimestampTag(newCommit.Date)}" };
                        pluginForkEmbed.LastCommitMessage = new EmbedField { IsInline = false, FieldName = "Last Commit Message", FieldDesc = $"```\n{processedCommitMessage}\n```" };
                    }
                    else
                    {
                        pluginForkEmbed.LastCommitMessage = new EmbedField { IsInline = false, FieldName = "Last Commit Message", FieldDesc = "```\nN/A\n```" };
                    }

                    if (newForkReleases != null && newForkReleases.Any())
                    {
                        var latestRelease = newForkReleases.MaxBy(x => x.Date);
                        var displayTitle = latestRelease.Title.Replace("`", "");

                        pluginForkEmbed.HasRelease = true;
                        pluginForkEmbed.NewLatestRelease = new EmbedField { IsInline = true, FieldName = "New Latest Release", FieldDesc = $"```\n{displayTitle}\n```" };
                        pluginForkEmbed.NewLatestReleaseTag = new EmbedField
                        { IsInline = true, FieldName = "Release Tag", FieldDesc = $"[{latestRelease.Id}](<{GithubUrls.Release(fork.Author, fork.Name, latestRelease.Id)}>)" };

                        if (newCommit == null)
                        {
                            pluginForkEmbed.CommitWhen = new EmbedField { IsInline = true, FieldName = "Last Release Date", FieldDesc = $"{ToUnixTimestampTag(latestRelease.Date)}" };
                        }
                    }

                    payLoad.EmbeddedMessages.Add(pluginForkEmbed);
                }
            }
        }

        payLoad.EmbeddedMessages.Add(new EmbedMessages { IsRepoEmbed = false, Title = "Data generated using <https://instantsc.github.io/PluginBrowser>, check it out for the full plugin list", EmbedColor = 5814783 });

        var discordEmbedBatches = payLoad.EmbeddedMessages
            .Select((message, index) =>
            {
                object embed;
                if (message.IsRepoEmbed)
                {
                    var author = new { name = message.Author, url = message.AuthorURL };
                    var fields = new[]
                    {
                            new { name = message.Fork.FieldName, value = message.Fork.FieldDesc, inline = message.Fork.IsInline },
                            new { name = message.CommitWhen.FieldName, value = message.CommitWhen.FieldDesc, inline = message.CommitWhen.IsInline },
                            new { name = message.LastCommitMessage.FieldName, value = message.LastCommitMessage.FieldDesc, inline = message.LastCommitMessage.IsInline },
                            new { name = message.NewLatestRelease.FieldName, value = message.NewLatestRelease.FieldDesc, inline = message.NewLatestRelease.IsInline },
                            new { name = message.NewLatestReleaseTag.FieldName, value = message.NewLatestReleaseTag.FieldDesc, inline = message.NewLatestReleaseTag.IsInline }
                    }
                    .Where(field => !string.IsNullOrEmpty(field.name) && !string.IsNullOrEmpty(field.value))
                    .ToArray();

                    embed = new { author, fields, color = message.EmbedColor };
                }
                else
                {
                    embed = new { title = message.Title, description = message.Body, color = message.EmbedColor };
                }
                return new { embed, index };
            })
            .GroupBy(x => x.index / 10)
            .Select(g => g.Select(x => x.embed).ToArray())
            .ToList();

        using (var httpClient = new HttpClient())
        {
            const string warningMessage = "__**Warning! This message was formed automatically and contains content from third-party sources. Proceed with caution**__";

            var tasks = discordEmbedBatches.Select((embedBatch, batchIndex) =>
            {
                var payload = new
                {
                    content = batchIndex == 0 ? warningMessage : string.Empty,
                    embeds = embedBatch
                };
                return httpClient.PostAsJsonAsync(releaseWebHook, payload);
            }).ToList();

            await Task.WhenAll(tasks);
        }
    }

    private static string ToUnixTimestampTag(DateTime dateTime)
    {
        try
        {
            return $"<t:{((DateTimeOffset)dateTime).ToUnixTimeSeconds()}>";
        }
        catch
        {
            return dateTime.Format();
        }
    }

    private static string SubstringBefore(string s, char[] symbols)
    {
        var firstIndex = s.IndexOfAny(symbols);
        if (firstIndex == -1) return s;
        return s.Substring(0, firstIndex);
    }
}

public class WebhookPayload
{
    public List<EmbedMessages> EmbeddedMessages { get; set; } = [];
}

public class EmbedMessages
{
    public int EmbedColor { get; set; } = 22657;
    public bool IsRepoEmbed { get; set; } = true;
    public string Author { get; set; } = string.Empty;
    public string AuthorURL { get; set; } = string.Empty;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsNewFork { get; set; }
    public EmbedField Fork { get; set; } = new();
    public EmbedField CommitWhen { get; set; } = new();
    public EmbedField LastCommitMessage { get; set; } = new();
    public bool HasRelease { get; set; }
    public EmbedField NewLatestRelease { get; set; } = new();
    public EmbedField NewLatestReleaseTag { get; set; } = new();
}

public class EmbedField
{
    public bool IsInline { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string FieldDesc { get; set; } = string.Empty;
}
