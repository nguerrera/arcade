// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Octokit;

namespace Microsoft.DotNet.DarcLib
{
    public class GitHubClient : IGitRepo
    {
        private const string GitHubApiUri = "https://api.github.com";
        private const string DarcLibVersion = "1.0.0";
        private static readonly ProductHeaderValue _product;

        private static readonly Regex repoUriPattern = new Regex(@"^/(?<owner>[^/]+)/(?<repo>[^/]+)/?$");

        private static readonly Regex prUriPattern =
            new Regex(@"^/repos/(?<owner>[^/]+)/(?<repo>[^/]+)/pulls/(?<id>\d+)$");

        private readonly Lazy<Octokit.GitHubClient> _lazyClient;
        private readonly ILogger _logger;
        private readonly string _personalAccessToken;
        private readonly JsonSerializerSettings _serializerSettings;
        private readonly string _userAgent = $"DarcLib-{DarcLibVersion}";

        static GitHubClient()
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
            _product = new ProductHeaderValue("DarcLib", version);
        }

        public GitHubClient(string accessToken, ILogger logger)
        {
            _personalAccessToken = accessToken;
            _logger = logger;
            _serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
            _lazyClient = new Lazy<Octokit.GitHubClient>(CreateGitHubClientClient);
        }

        public Octokit.GitHubClient Client => _lazyClient.Value;

        /// <summary>
        ///     Retrieve the contents of a file in a repository.
        /// </summary>
        /// <param name="filePath">Path of file (relative to repo root)</param>
        /// <param name="repoUri">URI of repo containing the file.</param>
        /// <param name="branchOrCommit">Branch or commit to obtain file from.</param>
        /// <returns>Content of file.</returns>
        public async Task<string> GetFileContentsAsync(string filePath, string repoUri, string branchOrCommit)
        {
            _logger.LogInformation(
                $"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branchOrCommit}'...");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);

            HttpResponseMessage response;
            try
            {
                response = await ExecuteRemoteGitCommand(
                    HttpMethod.Get,
                    $"repos/{ownerAndRepo}/contents/{filePath}?ref={branchOrCommit}",
                    _logger);
            }
            catch (HttpRequestException reqEx) when (reqEx.Message.Contains("404 (Not Found)"))
            {
                throw new DependencyFileNotFoundException(filePath, repoUri, branchOrCommit, reqEx);
            }

            _logger.LogInformation(
                $"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branchOrCommit}' succeeded!");

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            string content = responseContent["content"].ToString();

            return this.GetDecodedContent(content);
        }

        /// <summary>
        ///     Create a new branch in a repo.
        /// </summary>
        /// <param name="repoUri">Repository to create branch in.</param>
        /// <param name="newBranch">New branch name.</param>
        /// <param name="baseBranch">Branch to create @newBranch off of.</param>
        /// <returns></returns>
        public async Task CreateBranchAsync(string repoUri, string newBranch, string baseBranch)
        {
            _logger.LogInformation(
                $"Verifying if '{newBranch}' branch exist in repo '{repoUri}'. If not, we'll create it...");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            string latestSha = await GetLastCommitShaAsync(ownerAndRepo, baseBranch);
            string body;

            string gitRef = $"refs/heads/{newBranch}";
            var githubRef = new GitHubRef(gitRef, latestSha);
            HttpResponseMessage response = null;

            try
            {
                response = await ExecuteRemoteGitCommand(
                    HttpMethod.Get,
                    $"repos/{ownerAndRepo}/branches/{newBranch}",
                    _logger);

                githubRef.Force = true;
                body = JsonConvert.SerializeObject(githubRef, _serializerSettings);
                await ExecuteRemoteGitCommand(
                    new HttpMethod("PATCH"),
                    $"repos/{ownerAndRepo}/git/{gitRef}",
                    _logger,
                    body);
            }
            catch (HttpRequestException exc) when (exc.Message.Contains(((int) HttpStatusCode.NotFound).ToString()))
            {
                _logger.LogInformation($"'{newBranch}' branch doesn't exist. Creating it...");

                body = JsonConvert.SerializeObject(githubRef, _serializerSettings);
                response = await ExecuteRemoteGitCommand(
                    HttpMethod.Post,
                    $"repos/{ownerAndRepo}/git/refs",
                    _logger,
                    body);

                _logger.LogInformation($"Branch '{newBranch}' created in repo '{repoUri}'!");

                return;
            }
            catch (HttpRequestException exc)
            {
                _logger.LogError(
                    $"Checking if '{newBranch}' branch existed in repo '{repoUri}' failed with '{exc.Message}'");

                throw;
            }

            _logger.LogInformation($"Branch '{newBranch}' exists.");
        }

        /// <summary>
        ///     Commit new changes to a repo
        /// </summary>
        /// <param name="filesToCommit">Files to update</param>
        /// <param name="repoUri">Repository uri to push changes to</param>
        /// <param name="branch">Repository branch to push changes to</param>
        /// <param name="commitMessage">Commit message for new commit.</param>
        /// <returns></returns>
        public async Task PushFilesAsync(
            List<GitFile> filesToCommit,
            string repoUri,
            string branch,
            string commitMessage)
        {
            using (_logger.BeginScope("Pushing files to {branch}", branch))
            {
                (string owner, string repo) = ParseRepoUri(repoUri);

                string baseCommitSha = await Client.Repository.Commit.GetSha1(owner, repo, branch);
                Octokit.Commit baseCommit = await Client.Git.Commit.Get(owner, repo, baseCommitSha);
                TreeResponse baseTree = await Client.Git.Tree.Get(owner, repo, baseCommit.Tree.Sha);
                TreeResponse newTree = await CreateGitHubTreeAsync(owner, repo, filesToCommit, baseTree);
                Octokit.Commit newCommit = await Client.Git.Commit.Create(
                    owner,
                    repo,
                    new NewCommit(commitMessage, newTree.Sha, baseCommit.Sha));
                await Client.Git.Reference.Update(owner, repo, $"heads/{branch}", new ReferenceUpdate(newCommit.Sha));
            }
        }

        public async Task<IEnumerable<int>> SearchPullRequestsAsync(
            string repoUri,
            string pullRequestBranch,
            PrStatus status,
            string keyword = null,
            string author = null)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            var query = new StringBuilder();

            if (!string.IsNullOrEmpty(keyword))
            {
                query.Append(keyword);
                query.Append("+");
            }

            query.Append($"repo:{ownerAndRepo}+head:{pullRequestBranch}+type:pr+is:{status.ToString().ToLower()}");

            if (!string.IsNullOrEmpty(author))
            {
                query.Append($"+author:{author}");
            }

            HttpResponseMessage response = await ExecuteRemoteGitCommand(
                HttpMethod.Get,
                $"search/issues?q={query}",
                _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());
            JArray items = JArray.Parse(responseContent["items"].ToString());

            IEnumerable<int> prs = items.Select(r => r["number"].ToObject<int>());

            return prs;
        }

        public async Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await ExecuteRemoteGitCommand(HttpMethod.Get, url, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            if (Enum.TryParse(responseContent["state"].ToString(), true, out PrStatus status))
            {
                if (status == PrStatus.Open)
                {
                    return status;
                }

                if (status == PrStatus.Closed)
                {
                    if (bool.TryParse(responseContent["merged"].ToString(), out bool merged))
                    {
                        if (merged)
                        {
                            return PrStatus.Merged;
                        }
                    }

                    return PrStatus.Closed;
                }
            }

            return PrStatus.None;
        }

        public async Task<string> GetPullRequestRepo(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await ExecuteRemoteGitCommand(HttpMethod.Get, url, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            return responseContent["base"]["repo"]["html_url"].ToString();
        }

        public async Task<string> CreatePullRequestAsync(
            string repoUri,
            string mergeWithBranch,
            string sourceBranch,
            string title = null,
            string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(
                repoUri,
                mergeWithBranch,
                sourceBranch,
                HttpMethod.Post,
                title,
                description);
            return linkToPullRquest;
        }

        public async Task<string> UpdatePullRequestAsync(
            string pullRequestUri,
            string mergeWithBranch,
            string sourceBranch,
            string title = null,
            string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(
                pullRequestUri,
                mergeWithBranch,
                sourceBranch,
                new HttpMethod("PATCH"),
                title,
                description);
            return linkToPullRquest;
        }

        public async Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters)
        {
            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);

            var mergePullRequest = new MergePullRequest
            {
                Sha = parameters.CommitToMerge,
                MergeMethod = parameters.SquashMerge ? PullRequestMergeMethod.Squash : PullRequestMergeMethod.Merge
            };

            PullRequest pr = await Client.PullRequest.Get(owner, repo, id);
            await Client.PullRequest.Merge(owner, repo, id, mergePullRequest);

            if (parameters.DeleteSourceBranch)
            {
                await Client.Git.Reference.Delete(owner, repo, $"heads/{pr.Head.Ref}");
            }
        }

        public async Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message)
        {
            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);

            IssueComment comment = await Client.Issue.Comment.Create(owner, repo, id, message);

            return comment.Id.ToString();
        }

        public async Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message)
        {
            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);
            if (!int.TryParse(commentId, out int commentIdValue))
            {
                throw new ArgumentException("The comment id '{commentId}' is in an invalid format", nameof(commentId));
            }

            await Client.Issue.Comment.Update(owner, repo, commentIdValue, message);
        }

        public async Task<List<GitFile>> GetFilesForCommitAsync(string repoUri, string commit, string path)
        {
            path = path.Replace('\\', '/');
            path = path.TrimStart('/').TrimEnd('/');

            (string owner, string repo) = ParseRepoUri(repoUri);

            TreeResponse pathTree = await GetTreeForPathAsync(owner, repo, commit, path);

            TreeResponse recursiveTree = await GetRecursiveTreeAsync(owner, repo, pathTree.Sha);

            GitFile[] files = await Task.WhenAll(
                recursiveTree.Tree.Where(treeItem => treeItem.Type == TreeType.Blob)
                    .Select(
                        async treeItem =>
                        {
                            Blob blob = await Client.Git.Blob.Get(owner, repo, treeItem.Sha);
                            return new GitFile(
                                path + "/" + treeItem.Path,
                                blob.Content,
                                blob.Encoding == EncodingType.Base64 ? "base64" : "utf-8") {Mode = treeItem.Mode};
                        }));
            return files.ToList();
        }

        /*public async Task GetCommitMapForPathAsync(string repoUri, string branch, string buildCommit, List<GitFile> files, string pullRequestBaseBranch, string path)
        {
            if (path.EndsWith("/"))
            {
                path = path.Substring(0, path.Length - 1);
            }
            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{buildCommit}'");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/contents/{path}?ref={buildCommit}", _logger);

            List<GitHubContent> contents = JsonConvert.DeserializeObject<List<GitHubContent>>(await response.Content.ReadAsStringAsync());

            foreach (GitHubContent content in contents)
            {
                if (content.Type == GitHubContentType.File)
                {
                    if (!GitFileManager.DependencyFiles.Contains(content.Path))
                    {
                        string fileContent = await GetFileContentsAsync(content.Path, repoUri, );
                        GitFile gitCommit = new GitFile(content.Path, fileContent);
                        files.Add(gitCommit);
                    }
                }
                else
                {
                    await GetCommitMapForPathAsync(repoUri, branch, buildCommit, files, pullRequestBaseBranch, content.Path);
                }
            }

            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{buildCommit}' succeeded!");
        }*/

        /// <summary>
        ///     Determine whether a file exists at a specified branch or commit.
        /// </summary>
        /// <param name="repoUri">Repository uri</param>
        /// <param name="filePath">Path of file</param>
        /// <param name="branch">Branch or commit in to check.</param>
        /// <returns>True if the file exists, false otherwise.</returns>
        public async Task<bool> FileExistsAsync(string repoUri, string filePath, string branch)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response;

            try
            {
                response = await ExecuteRemoteGitCommand(
                    HttpMethod.Get,
                    $"repos/{ownerAndRepo}/contents/{filePath}?ref={branch}",
                    _logger);
            }
            catch (HttpRequestException exc) when (exc.Message.Contains(((int) HttpStatusCode.NotFound).ToString()))
            {
                return false;
            }

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            return string.IsNullOrEmpty(content["sha"].ToString());
        }

        /// <summary>
        ///     Get the latest commit sha on a given branch for a repo
        /// </summary>
        /// <param name="repoUri">Repository to get latest commit in.</param>
        /// <param name="branch">Branch to get get latest commit in.</param>
        /// <returns>Latest commit sha.</returns>
        public async Task<string> GetLastCommitShaAsync(string repoUri, string branch)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response = await ExecuteRemoteGitCommand(
                HttpMethod.Get,
                $"repos/{ownerAndRepo}/commits/{branch}",
                _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            if (content == null)
            {
                throw new Exception($"No commits found in branch '{branch}' of repo '{ownerAndRepo}'!");
            }

            return content["sha"].ToString();
        }

        public async Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl)
        {
            string url = $"{GetPrPartialAbsolutePath(pullRequestUrl)}/commits";

            HttpResponseMessage response = await ExecuteRemoteGitCommand(HttpMethod.Get, url, _logger);
            JArray content = JArray.Parse(await response.Content.ReadAsStringAsync());
            JToken lastCommit = content.Last;
            string lastCommitSha = lastCommit["sha"].ToString();

            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);
            response = await ExecuteRemoteGitCommand(
                HttpMethod.Get,
                $"repos/{owner}/{repo}/commits/{lastCommitSha}/status",
                _logger);

            JObject statusContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            IList<Check> statuses = new List<Check>();
            foreach (JToken status in statusContent["statuses"])
            {
                if (Enum.TryParse(status["state"].ToString(), true, out CheckState state))
                {
                    statuses.Add(new Check(state, status["context"].ToString(), status["target_url"].ToString()));
                }
            }

            return statuses;
        }

        /// <summary>
        ///     Get the source branch for the pull request.
        /// </summary>
        /// <param name="pullRequestUrl">url of pull request</param>
        /// <returns>Branch of pull request.</returns>
        public async Task<string> GetPullRequestSourceBranch(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await ExecuteRemoteGitCommand(HttpMethod.Get, url, _logger);
            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            return content["head"]["ref"].ToString();
        }

        public async Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl)
        {
            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);

            IReadOnlyList<PullRequestCommit> commits = await Client.PullRequest.Commits(owner, repo, id);

            return commits.Select(c => new Commit(c.Author.Name, c.Sha)).ToList();
        }

        private Octokit.GitHubClient CreateGitHubClientClient()
        {
            return new Octokit.GitHubClient(_product) {Credentials = new Credentials(_personalAccessToken)};
        }

        /// <summary>
        ///     Create an http client for accessing github
        /// </summary>
        /// <param name="versionOverride">Optional API version override.</param>
        /// <returns></returns>
        public HttpClient CreateHttpClient(string versionOverride = null)
        {
            var client = new HttpClient {BaseAddress = new Uri(GitHubApiUri)};
            client.DefaultRequestHeaders.Add("Authorization", $"Token {_personalAccessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", _userAgent);

            return client;
        }

        /// <summary>
        ///     Executes a remote git command on Azure DevOps.
        /// </summary>
        /// <param name="accountName">Account name used for request</param>
        /// <param name="projectName">Project name used for the request</param>
        /// <param name="apiPath">Path to access (relative to base URI).</param>
        /// <returns></returns>
        private async Task<HttpResponseMessage> ExecuteRemoteGitCommand(
            HttpMethod method,
            string apiPath,
            ILogger logger,
            string body = null,
            string versionOverride = null)
        {
            // Construct the API URI
            using (HttpClient client = CreateHttpClient(versionOverride))
            {
                var requestManager = new HttpRequestManager(client, method, apiPath, logger, body, versionOverride);

                return await requestManager.ExecuteAsync();
            }
        }

        public async Task CommentOnPullRequestAsync(string pullRequestUrl, string message)
        {
            var comment = new GitHubComment(message);

            string body = JsonConvert.SerializeObject(comment, _serializerSettings);

            (string owner, string repo, int id) = ParsePullRequestUri(pullRequestUrl);

            await ExecuteRemoteGitCommand(HttpMethod.Post, $"repos/{owner}/{repo}/issues/{id}/comments", _logger, body);
        }

        private async Task<TreeResponse> GetRecursiveTreeAsync(string owner, string repo, string treeSha)
        {
            TreeResponse tree = await Client.Git.Tree.GetRecursive(owner, repo, treeSha);
            if (tree.Truncated)
            {
                throw new NotSupportedException(
                    $"The git repository is too large for the github api. Getting recursive tree '{treeSha}' returned truncated results.");
            }

            return tree;
        }

        private async Task<TreeResponse> GetTreeForPathAsync(string owner, string repo, string commitSha, string path)
        {
            var pathSegments = new Queue<string>(path.Split('/', '\\'));
            var currentPath = new List<string>();
            Octokit.Commit commit = await Client.Git.Commit.Get(owner, repo, commitSha);

            string treeSha = commit.Tree.Sha;

            while (true)
            {
                TreeResponse tree = await Client.Git.Tree.Get(owner, repo, treeSha);
                if (tree.Truncated)
                {
                    throw new NotSupportedException(
                        $"The git repository is too large for the github api. Getting tree '{treeSha}' returned truncated results.");
                }

                if (pathSegments.Count < 1)
                {
                    return tree;
                }

                string subfolder = pathSegments.Dequeue();
                currentPath.Add(subfolder);
                TreeItem subfolderItem = tree.Tree.Where(ti => ti.Type == TreeType.Tree)
                    .FirstOrDefault(ti => ti.Path == subfolder);
                if (subfolderItem == null)
                {
                    throw new DirectoryNotFoundException(
                        $"The path '{string.Join("/", currentPath)}' could not be found.");
                }

                treeSha = subfolderItem.Sha;
            }
        }

        private static string GetOwnerAndRepo(string uri, Regex pattern)
        {
            var u = new UriBuilder(uri);
            Match match = pattern.Match(u.Path);
            if (!match.Success)
            {
                return null;
            }

            return $"{match.Groups["owner"]}/{match.Groups["repo"]}";
        }

        public static string GetOwnerAndRepoFromRepoUri(string repoUri)
        {
            return GetOwnerAndRepo(repoUri, repoUriPattern);
        }

        public static (string owner, string repo) ParseRepoUri(string uri)
        {
            var u = new UriBuilder(uri);
            Match match = repoUriPattern.Match(u.Path);
            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["owner"].Value, match.Groups["repo"].Value);
        }

        public static (string owner, string repo, int id) ParsePullRequestUri(string uri)
        {
            var u = new UriBuilder(uri);
            Match match = prUriPattern.Match(u.Path);
            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["owner"].Value, match.Groups["repo"].Value, int.Parse(match.Groups["id"].Value));
        }

        private async Task<string> CreateOrUpdatePullRequestAsync(
            string uri,
            string mergeWithBranch,
            string sourceBranch,
            HttpMethod method,
            string title = null,
            string description = null)
        {
            string requestUri;

            title = !string.IsNullOrEmpty(title)
                ? $"{PullRequestProperties.TitleTag} {title}"
                : PullRequestProperties.Title;
            description = description ?? PullRequestProperties.Description;

            var pullRequest = new GitHubPullRequest(title, description, sourceBranch, mergeWithBranch);

            string body = JsonConvert.SerializeObject(pullRequest, _serializerSettings);

            if (method == HttpMethod.Post)
            {
                string ownerAndRepo = GetOwnerAndRepoFromRepoUri(uri);
                requestUri = $"repos/{ownerAndRepo}/pulls";
            }
            else
            {
                requestUri = GetPrPartialAbsolutePath(uri);
            }

            HttpResponseMessage response = await ExecuteRemoteGitCommand(method, requestUri, _logger, body);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            Console.WriteLine($"Browser ready link for this PR is: {content["html_url"]}");

            return content["url"].ToString();
        }

        private string GetPrPartialAbsolutePath(string prLink)
        {
            var uri = new Uri(prLink);
            return uri.PathAndQuery;
        }


        private async Task<TreeResponse> CreateGitHubTreeAsync(
            string owner,
            string repo,
            IEnumerable<GitFile> filesToCommit,
            TreeResponse baseTree)
        {
            var newTree = new NewTree {BaseTree = baseTree.Sha};

            foreach (GitFile file in filesToCommit)
            {
                BlobReference newBlob = await Client.Git.Blob.Create(
                    owner,
                    repo,
                    new NewBlob
                    {
                        Content = file.Content,
                        Encoding = file.ContentEncoding == "base64" ? EncodingType.Base64 : EncodingType.Utf8
                    });
                newTree.Tree.Add(
                    new NewTreeItem {Path = file.FilePath, Sha = newBlob.Sha, Mode = file.Mode, Type = TreeType.Blob});
            }

            return await Client.Git.Tree.Create(owner, repo, newTree);
        }

        private async Task<string> PushCommitAsync(
            string ownerAndRepo,
            string commitMessage,
            string treeSha,
            string baseTreeSha)
        {
            var gitHubCommit = new GitHubCommit
            {
                Message = commitMessage, Tree = treeSha, Parents = new List<string> {baseTreeSha}
            };

            string body = JsonConvert.SerializeObject(gitHubCommit, _serializerSettings);
            HttpResponseMessage response = await ExecuteRemoteGitCommand(
                HttpMethod.Post,
                $"repos/{ownerAndRepo}/git/commits",
                _logger,
                body);
            JToken parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);
            return parsedResponse["sha"].ToString();
        }

        private async Task<List<GitHubTreeItem>> GetTreeItems(string repoUri, string commit)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response = await ExecuteRemoteGitCommand(
                HttpMethod.Get,
                $"repos/{ownerAndRepo}/commits/{commit}",
                _logger);
            JToken parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);
            var treeUrl = new Uri(parsedResponse["commit"]["tree"]["url"].ToString());

            response = await ExecuteRemoteGitCommand(HttpMethod.Get, $"{treeUrl.PathAndQuery}?recursive=1", _logger);
            parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);

            JArray tree = JArray.Parse(parsedResponse["tree"].ToString());

            var treeItems = new List<GitHubTreeItem>();

            foreach (JToken item in tree)
            {
                var treeItem = new GitHubTreeItem
                {
                    Mode = item["mode"].ToString(), Path = item["path"].ToString(), Type = item["type"].ToString()
                };

                treeItems.Add(treeItem);
            }

            return treeItems;
        }
    }
}
