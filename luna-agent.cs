#:package Microsoft.EntityFrameworkCore.Sqlite@9.0.0
#:package SlackNet@0.17.9
#:package LibGit2Sharp@0.30.0
#:package Octokit@13.0.1
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlackNet;
using SlackNet.Events;
using SlackNet.SocketMode;
using SlackNet.WebApi;
using LibGit2Sharp;
using Octokit;

// ============================================================================
// Configuration
// ============================================================================

var slackConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".luna", "luna.env");
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".luna-agent", "tasks.db");
var slackBotToken = "";
var slackAppToken = "";
var agentChannelId = "";
var agentUserId = "";
var userGithubName = "";

// AI Configuration
const double OllamaTemperature = 0.7;
const int OllamaMaxTokens = 2048;
const string OllamaDefaultModel = "gemma3:4b";
const int OllamaDefaultTimeoutMinutes = 30; // Default timeout for Ollama HTTP requests (30 minutes for long-running tasks)

// Task Processing Configuration
const int MaxTaskIterations = 100;
const int MaxContextHistoryEntryLength = 5000;
const int MaxErrorMessagePreviewLength = 2000;
const int MaxIterationTimeSeconds = 180; // 3 minutes per iteration
const int MaxOllamaRetries = 3; // Number of retries for Ollama failures
const int OllamaRetryBaseDelayMs = 2000; // Base delay for retry exponential backoff
const int IterationContinueDelayMs = 2000; // Delay before continuing to next iteration
const int OllamaFailureDelayMs = 5000; // Delay after Ollama failure before retry

// Markdown cleanup constants
const string MarkdownJsonBlockPrefix = "```json";
const string MarkdownCodeBlockPrefix = "```";

// Load Slack tokens from file
if (System.IO.File.Exists(slackConfigFile))
{
    foreach (var line in System.IO.File.ReadAllLines(slackConfigFile))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
        var parts = line.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim();
            if (key == "SLACK_BOT_TOKEN" || key == "xoxb") slackBotToken = value;
            if (key == "SLACK_APP_TOKEN" || key == "xapp") slackAppToken = value;
            if (key == "SLACK_CHANNEL_ID") agentChannelId = value;
            if (key == "UserGithubName") userGithubName = value;
        }
    }
}

if (string.IsNullOrEmpty(slackBotToken) || string.IsNullOrEmpty(slackAppToken) || string.IsNullOrEmpty(agentChannelId))
{
    Console.WriteLine("ERROR: Missing required configuration. Please configure ~/.luna/luna.env with:");
    Console.WriteLine("  SLACK_BOT_TOKEN=xoxb-...");
    Console.WriteLine("  SLACK_APP_TOKEN=xapp-...");
    Console.WriteLine("  SLACK_CHANNEL_ID=C...");
    return;
}

Console.WriteLine("=== LUNA Agent Starting ===");
Console.WriteLine($"Database: {dbPath}");
Console.WriteLine($"Agent Channel ID: {agentChannelId}");

// Set database path for AgentDbContext
AgentDbContext.DbPath = dbPath;

// Initialize database
using (var db = new AgentDbContext())
{
    db.Database.EnsureCreated();
}

// Clean up any stale containers from previous runs
Console.WriteLine("🧹 Cleaning up stale Docker containers...");
await CleanupStaleContainers();

// ============================================================================
// Task Queue Management
// ============================================================================

var taskQueue = new Queue<WorkTask>();
WorkTask? currentTask = null;
var queueLock = new object();

// Configure Ollama HTTP client with extended timeout for long-running AI tasks
// Can be overridden via OLLAMA_TIMEOUT_MINUTES environment variable (max: 120 minutes)
const int MaxOllamaTimeoutMinutes = 120; // Maximum allowed timeout to prevent indefinite hangs
var ollamaTimeoutMinutes = OllamaDefaultTimeoutMinutes;
var ollamaTimeoutEnv = Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_MINUTES");
if (!string.IsNullOrEmpty(ollamaTimeoutEnv) && int.TryParse(ollamaTimeoutEnv, out var customTimeout) && customTimeout > 0)
{
    if (customTimeout > MaxOllamaTimeoutMinutes)
    {
        Console.WriteLine($"⚠️  OLLAMA_TIMEOUT_MINUTES value {customTimeout} exceeds maximum {MaxOllamaTimeoutMinutes}, using maximum");
        ollamaTimeoutMinutes = MaxOllamaTimeoutMinutes;
    }
    else
    {
        ollamaTimeoutMinutes = customTimeout;
    }
    Console.WriteLine($"Using custom Ollama timeout: {ollamaTimeoutMinutes} minutes");
}
else
{
    Console.WriteLine($"Using default Ollama timeout: {ollamaTimeoutMinutes} minutes");
}

var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromMinutes(ollamaTimeoutMinutes);

// ============================================================================
// Helper Functions
// ============================================================================

async Task LogThought(int taskId, int iteration, ThoughtType type, string content, string? actionType = null, string? actionDetails = null, bool streamToUser = true)
{
    using var db = new AgentDbContext();
    var thought = new AgenticThought
    {
        TaskId = taskId,
        IterationNumber = iteration,
        Type = type,
        Timestamp = DateTime.Now,
        Content = content,
        ActionType = actionType,
        ActionDetails = actionDetails,
        StreamedToUser = streamToUser
    };
    
    db.Thoughts.Add(thought);
    await db.SaveChangesAsync();
}

async Task LogToDb(int taskId, string message)
{
    using var db = new AgentDbContext();
    var task = await db.Tasks.FindAsync(taskId);
    if (task != null)
    {
        task.Log += $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
        task.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
    }
}

async Task UpdateTaskStatus(int taskId, TaskStatus status, string? errorMessage = null, string? result = null)
{
    using var db = new AgentDbContext();
    var task = await db.Tasks.FindAsync(taskId);
    if (task != null)
    {
        task.Status = status;
        task.UpdatedAt = DateTime.Now;
        if (status == TaskStatus.Running && !task.StartedAt.HasValue)
            task.StartedAt = DateTime.Now;
        if (status == TaskStatus.Completed || status == TaskStatus.Failed || status == TaskStatus.Stopped)
            task.CompletedAt = DateTime.Now;
        if (errorMessage != null)
            task.ErrorMessage = errorMessage;
        if (result != null)
            task.Result = result;
        await db.SaveChangesAsync();
    }
}

async Task SendSlackMessage(ISlackApiClient slack, string message)
{
    try
    {
        if (!string.IsNullOrEmpty(agentChannelId))
        {
            await slack.Chat.PostMessage(new Message
            {
                Channel = agentChannelId,
                Text = message
            });
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending Slack message: {ex.Message}");
    }
}

string FormatOutput(string output, string label = "Output", bool useCodeBlock = true)
{
    var formattedOutput = useCodeBlock ? $"```{output}```" : output;
    return $"📤 **{label}:**\n{formattedOutput}";
}

string CleanMarkdownFromResponse(string response)
{
    var cleaned = response.Trim();
    
    if (cleaned.StartsWith(MarkdownJsonBlockPrefix)) 
        cleaned = cleaned.Substring(MarkdownJsonBlockPrefix.Length).TrimStart();
    else if (cleaned.StartsWith(MarkdownCodeBlockPrefix)) 
        cleaned = cleaned.Substring(MarkdownCodeBlockPrefix.Length).TrimStart();
    
    if (cleaned.EndsWith(MarkdownCodeBlockPrefix)) 
        cleaned = cleaned.Substring(0, cleaned.Length - MarkdownCodeBlockPrefix.Length).TrimEnd();
    
    return cleaned.Trim();
}

async Task<string> CallOllama(string prompt, string? model = null, int retryCount = 0)
{
    try
    {
        var requestBody = new
        {
            model = model ?? OllamaDefaultModel,
            prompt = prompt,
            stream = false,
            options = new { temperature = OllamaTemperature, num_predict = OllamaMaxTokens }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("http://localhost:11434/api/generate", content);

        if (response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync();
            
            // Validate response is not empty
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                Console.WriteLine("⚠️  Ollama returned empty response");
                if (retryCount < MaxOllamaRetries)
                {
                    Console.WriteLine($"   Retrying... (attempt {retryCount + 1}/{MaxOllamaRetries})");
                    await Task.Delay(OllamaRetryBaseDelayMs * (retryCount + 1)); // Exponential backoff
                    return await CallOllama(prompt, model, retryCount + 1);
                }
                return "";
            }
            
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement.GetProperty("response").GetString() ?? "";
            }
            catch (JsonException jex)
            {
                Console.WriteLine($"⚠️  Failed to parse Ollama JSON response: {jex.Message}");
                Console.WriteLine($"   Response was: {responseJson.Substring(0, Math.Min(200, responseJson.Length))}...");
                if (retryCount < MaxOllamaRetries)
                {
                    Console.WriteLine($"   Retrying... (attempt {retryCount + 1}/{MaxOllamaRetries})");
                    await Task.Delay(OllamaRetryBaseDelayMs * (retryCount + 1)); // Exponential backoff
                    return await CallOllama(prompt, model, retryCount + 1);
                }
                return "";
            }
        }
        else
        {
            Console.WriteLine($"⚠️  Ollama HTTP error: {response.StatusCode}");
            if (retryCount < MaxOllamaRetries)
            {
                Console.WriteLine($"   Retrying... (attempt {retryCount + 1}/{MaxOllamaRetries})");
                await Task.Delay(OllamaRetryBaseDelayMs * (retryCount + 1)); // Exponential backoff
                return await CallOllama(prompt, model, retryCount + 1);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ollama error: {ex.Message}");
        if (retryCount < MaxOllamaRetries)
        {
            Console.WriteLine($"   Retrying... (attempt {retryCount + 1}/{MaxOllamaRetries})");
            await Task.Delay(OllamaRetryBaseDelayMs * (retryCount + 1)); // Exponential backoff
            return await CallOllama(prompt, model, retryCount + 1);
        }
    }
    return "";
}

async Task<string> RunCommand(string command, string workingDir = "")
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return $"Error (exit {process.ExitCode}): {error}\n{output}";
            
            return output;
        }
    }
    catch (Exception ex)
    {
        return $"Exception: {ex.Message}";
    }
    return "Failed to start process";
}

async Task<(bool success, string containerId, string output)> CreateTaskContainer(int taskId, string taskDescription)
{
    try
    {
        // Use unique container name with timestamp to avoid conflicts
        var uniqueSuffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var containerName = $"luna-task-{taskId}-{uniqueSuffix}";
        
        // Create a Docker container with necessary tools
        var createCommand = $"docker run -d --name {containerName} " +
                          $"-v /tmp/luna-task-{taskId}:/workspace " +
                          $"-w /workspace " +
                          $"ubuntu:22.04 tail -f /dev/null";
        
        var output = await RunCommand(createCommand);
        
        if (output.Contains("Error") || string.IsNullOrWhiteSpace(output))
        {
            return (false, "", output);
        }
        
        var containerId = output.Trim();
        
        // Install basic tools in the container
        await RunCommand($"docker exec {containerId} apt-get update -qq");
        await RunCommand($"docker exec {containerId} apt-get install -y -qq git curl wget build-essential");
        
        // Update task with container info
        using var db = new AgentDbContext();
        var task = await db.Tasks.FindAsync(taskId);
        if (task != null)
        {
            task.ContainerId = containerId;
            task.ContainerName = containerName;
            await db.SaveChangesAsync();
        }
        
        return (true, containerId, $"Container {containerName} created successfully");
    }
    catch (Exception ex)
    {
        return (false, "", $"Failed to create container: {ex.Message}");
    }
}

async Task<string> RunCommandInContainer(string containerId, string command)
{
    try
    {
        var dockerCommand = $"docker exec {containerId} bash -c \"{command.Replace("\"", "\\\"")}\"";
        return await RunCommand(dockerCommand);
    }
    catch (Exception ex)
    {
        return $"Exception running command in container: {ex.Message}";
    }
}

async Task<bool> StopAndRemoveContainer(string containerId)
{
    try
    {
        if (!string.IsNullOrEmpty(containerId))
        {
            await RunCommand($"docker stop {containerId}");
            await RunCommand($"docker rm {containerId}");
            return true;
        }
    }
    catch
    {
        // Ignore errors during cleanup
    }
    return false;
}

async Task<string> CopyFileToContainer(string containerId, string localPath, string containerPath)
{
    return await RunCommand($"docker cp {localPath} {containerId}:{containerPath}");
}

async Task<string> CopyFileFromContainer(string containerId, string containerPath, string localPath)
{
    return await RunCommand($"docker cp {containerId}:{containerPath} {localPath}");
}

async Task<string?> CreatePullRequest(int taskId, string branchName, string repoPath, string taskDescription)
{
    try
    {
        // Commit changes
        await RunCommand("git add .", repoPath);
        var commitMessage = $"LUNA Agent - Task #{taskId}: {taskDescription}";
        await RunCommand($"git commit -m \"{commitMessage}\"", repoPath);
        await LogToDb(taskId, "Changes committed");

        // Try to push and create PR using GitHub CLI if available
        var ghInstalled = await RunCommand("which gh");
        if (!string.IsNullOrEmpty(ghInstalled) && !ghInstalled.Contains("not found"))
        {
            // Push branch
            await RunCommand($"git push -u origin {branchName}", repoPath);
            await LogToDb(taskId, $"Pushed branch to origin");

            // Create PR
            // Note: Task description should not contain sensitive information as it will be visible in the PR
            var prBody = $"This PR was automatically generated by LUNA Agent for task #{taskId}.\n\n**Task Description:**\n{taskDescription}\n\n**Created:** {DateTime.Now}\n\n**Security Note:** This PR was created by an automated agent. Please review all changes carefully before merging.";
            var prOutput = await RunCommand($"gh pr create --title '{commitMessage}' --body '{prBody}' --head {branchName}", repoPath);
            
            // Extract PR URL from output
            var lines = prOutput.Split('\n');
            var prUrl = lines.FirstOrDefault(l => l.Contains("https://github.com/"));
            
            if (!string.IsNullOrEmpty(prUrl))
            {
                await LogToDb(taskId, $"Created PR: {prUrl}");
                return prUrl.Trim();
            }
        }
        else
        {
            await LogToDb(taskId, "GitHub CLI not available - branch created but PR not created automatically");
            return $"Branch {branchName} created (manual PR creation required)";
        }
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error creating PR: {ex.Message}");
    }
    return null;
}

async Task<string> DoOnlineResearch(string query)
{
    // For online research, we'll use the AI to formulate research
    // In a real implementation, this could use web scraping or search APIs
    var researchPrompt = $@"Research the following topic and provide a concise summary:
{query}

Provide key facts, recent developments, and relevant information.
Keep the response under 500 words and be direct - do not ask follow-up questions.";

    var result = await CallOllama(researchPrompt);
    return result;
}

async Task<(int cpu, int ram, double temp)> GetSystemStats()
{
    try
    {
        var cpuOut = await RunCommand("top -bn1 | grep 'Cpu(s)' | awk '{print int($2)}'");
        var cpu = int.TryParse(cpuOut.Trim(), out var c) ? c : 0;
        var ramOut = await RunCommand("free | grep Mem | awk '{print int($3/$2 * 100)}'");
        var ram = int.TryParse(ramOut.Trim(), out var r) ? r : 0;
        var tempOut = await RunCommand("vcgencmd measure_temp 2>/dev/null | grep -o '[0-9.]*' || echo '0'");
        var temp = double.TryParse(tempOut.Trim(), out var t) ? t : 0.0;
        return (cpu, ram, temp);
    }
    catch
    {
        return (0, 0, 0.0);
    }
}

async Task<(bool isRunning, string modelInfo)> GetOllamaStatusAsync()
{
    try
    {
        var response = await httpClient.GetAsync("http://localhost:11434/api/tags");
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var models = doc.RootElement.GetProperty("models");
            var modelCount = models.GetArrayLength();
            
            if (modelCount > 0)
            {
                var firstModel = models[0].GetProperty("name").GetString();
                return (true, $"{modelCount} model(s) loaded, primary: {firstModel}");
            }
            return (true, "Running (no models loaded)");
        }
    }
    catch
    {
        // Ollama not responding
    }
    return (false, "Not responding");
}

async Task CleanupStaleContainers()
{
    try
    {
        // List all containers matching luna-task pattern
        var output = await RunCommand("docker ps -a --filter=\"name=luna-task\" --format=\"{{.Names}}\"");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var containerNames = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var containerName in containerNames)
        {
            try
            {
                // Try to stop and remove each container
                await RunCommand($"docker stop {containerName} 2>/dev/null || true");
                await RunCommand($"docker rm {containerName} 2>/dev/null || true");
                Console.WriteLine($"🧹 Cleaned up stale container: {containerName}");
            }
            catch
            {
                // Ignore individual cleanup failures
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Error during container cleanup: {ex.Message}");
    }
}

// ============================================================================
// Task Deliverables Management
// ============================================================================

async Task<TaskClassification> ClassifyTaskWithAI(string description, int taskId)
{
    try
    {
        await LogToDb(taskId, "Classifying task using AI...");
        
        var prompt = $@"Analyze this task description and determine its classification.

Task: {description}

Respond with ONLY valid JSON (no markdown code blocks):
{{
  ""isCodeTask"": true or false,
  ""requiresNewRepo"": true or false,
  ""requiresExternalRepoAccess"": true or false,
  ""urlOfExternalGitHubRepo"": ""url or null""
}}

Guidelines:
- isCodeTask: true if the task involves writing, modifying, or working with code/scripts/programs
- requiresNewRepo: true if it's a code task that doesn't reference an existing repository
- requiresExternalRepoAccess: true if the task references an existing GitHub repository
- urlOfExternalGitHubRepo: extract the GitHub repo URL if mentioned (formats: github.com/owner/repo, owner/repo, or full URL), otherwise null

Examples:
""Write a Python script to calculate primes"" -> isCodeTask: true, requiresNewRepo: true, requiresExternalRepoAccess: false, urlOfExternalGitHubRepo: null
""Create an analysis of market trends"" -> isCodeTask: false, requiresNewRepo: false, requiresExternalRepoAccess: false, urlOfExternalGitHubRepo: null
""Fix the bug in dahln/LUNA repository"" -> isCodeTask: true, requiresNewRepo: false, requiresExternalRepoAccess: true, urlOfExternalGitHubRepo: ""dahln/LUNA""

Respond with ONLY the JSON object, no other text:";

        var aiResponse = await CallOllama(prompt);
        await LogToDb(taskId, $"AI classification response: {aiResponse}");
        
        // Clean up markdown code blocks if present
        var cleanedResponse = CleanMarkdownFromResponse(aiResponse);
        
        var classification = JsonDocument.Parse(cleanedResponse);
        var root = classification.RootElement;
        
        var result = new TaskClassification
        {
            IsCodeTask = root.GetProperty("isCodeTask").GetBoolean(),
            RequiresNewRepo = root.GetProperty("requiresNewRepo").GetBoolean(),
            RequiresExternalRepoAccess = root.GetProperty("requiresExternalRepoAccess").GetBoolean(),
            UrlOfExternalGitHubRepo = root.TryGetProperty("urlOfExternalGitHubRepo", out var urlProp) && urlProp.ValueKind != JsonValueKind.Null
                ? urlProp.GetString()
                : null
        };
        
        await LogToDb(taskId, $"Classification: IsCode={result.IsCodeTask}, NewRepo={result.RequiresNewRepo}, ExternalRepo={result.RequiresExternalRepoAccess}, URL={result.UrlOfExternalGitHubRepo}");
        
        return result;
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error classifying task with AI: {ex.Message}. Falling back to keyword detection.");
        
        // Fallback to keyword-based detection
        var lowerDesc = description.ToLower();
        var codeKeywords = new[] { 
            "code", "script", "program", "function", "class", "api", "app", "application",
            "python", "javascript", "java", "c#", "csharp", "go", "rust", "node", "react",
            "repo", "repository", "git", "github", "pull request", "pr", "commit"
        };
        
        var isCodeTask = codeKeywords.Any(keyword => lowerDesc.Contains(keyword));
        
        // Try to extract repo reference with regex as fallback
        string? repoUrl = null;
        var patterns = new[] {
            @"github\.com/([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)",
            @"(?:^|\s)([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)(?:\s|$)",
            @"https?://github\.com/([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)"
        };
        
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(description, pattern);
            if (match.Success)
            {
                repoUrl = match.Groups[1].Value;
                break;
            }
        }
        
        return new TaskClassification
        {
            IsCodeTask = isCodeTask,
            RequiresNewRepo = isCodeTask && repoUrl == null,
            RequiresExternalRepoAccess = repoUrl != null,
            UrlOfExternalGitHubRepo = repoUrl
        };
    }
}

async Task<bool> CloneOrPullRepo(string repoReference, string targetPath, int taskId)
{
    try
    {
        var repoUrl = repoReference.StartsWith("http") ? repoReference : $"https://github.com/{repoReference}";
        
        if (Directory.Exists(targetPath))
        {
            // Validate that it's actually a git repo and matches expected URL
            var gitDirPath = Path.Combine(targetPath, ".git");
            if (!Directory.Exists(gitDirPath))
            {
                await LogToDb(taskId, $"Directory {targetPath} exists but is not a git repo. Removing and cloning fresh.");
                Directory.Delete(targetPath, true);
                // Fall through to clone
            }
            else
            {
                // Verify remote URL matches
                var remoteUrlOutput = await RunCommand("git config --get remote.origin.url", targetPath);
                var existingUrl = remoteUrlOutput.Trim();
                
                if (!existingUrl.Contains(repoReference.Replace("https://github.com/", "")))
                {
                    await LogToDb(taskId, $"Directory {targetPath} has wrong remote URL. Removing and cloning fresh.");
                    Directory.Delete(targetPath, true);
                    // Fall through to clone
                }
                else
                {
                    // Repo already exists and is valid, pull latest
                    await LogToDb(taskId, $"Pulling latest changes from {repoReference}");
                    await RunCommand("git fetch origin", targetPath);
                    
                    // Determine the default branch
                    var defaultBranch = await RunCommand("git symbolic-ref refs/remotes/origin/HEAD 2>/dev/null | sed 's@^refs/remotes/origin/@@'", targetPath);
                    if (string.IsNullOrWhiteSpace(defaultBranch))
                    {
                        // Fallback: try to detect from remote branches
                        defaultBranch = await RunCommand("git branch -r | grep -o 'origin/main\\|origin/master' | head -1 | sed 's@origin/@@'", targetPath);
                    }
                    defaultBranch = defaultBranch.Trim();
                    if (string.IsNullOrWhiteSpace(defaultBranch))
                        defaultBranch = "main"; // Final fallback
                    
                    var pullOutput = await RunCommand($"git pull origin {defaultBranch}", targetPath);
                    if (pullOutput.Contains("fatal") || pullOutput.Contains("error"))
                    {
                        await LogToDb(taskId, $"Failed to pull from {defaultBranch}: {pullOutput}");
                        return false;
                    }
                    return true;
                }
            }
        }
        
        // Clone the repo (either first time or after cleanup)
        if (!Directory.Exists(targetPath))
        {
            await LogToDb(taskId, $"Cloning repository {repoReference}");
            var output = await RunCommand($"git clone {repoUrl} {targetPath}");
            
            if (output.Contains("fatal") || output.Contains("error"))
            {
                await LogToDb(taskId, $"Failed to clone repository: {output}");
                return false;
            }
            
            return true;
        }
        
        return false;
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error accessing repository: {ex.Message}");
        return false;
    }
}

async Task<string?> CreateNewGithubRepo(int taskId, string taskDescription, ISlackApiClient slack)
{
    try
    {
        var sanitized = new string(taskDescription.Take(30).Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        var repoName = $"luna-task-{taskId}-{sanitized}".ToLower();
        
        // Validate repo name matches GitHub requirements
        if (!System.Text.RegularExpressions.Regex.IsMatch(repoName, @"^[a-z0-9-]+$"))
        {
            await LogToDb(taskId, $"Invalid repository name generated: {repoName}");
            repoName = $"luna-task-{taskId}"; // Fallback to simple name
        }
        
        // Check if GH_TOKEN is available
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(ghToken))
        {
            await LogToDb(taskId, "GH_TOKEN not available - cannot create repository");
            await SendSlackMessage(slack, $"⚠️ Cannot create new repository - GitHub token not configured");
            return null;
        }
        
        // Create repo using GitHub CLI
        await LogToDb(taskId, $"Creating new GitHub repository: {repoName}");
        var output = await RunCommand($"gh repo create {repoName} --private --confirm");
        
        if (output.Contains("error") || output.Contains("failed"))
        {
            await LogToDb(taskId, $"Failed to create repository: {output}");
            return null;
        }
        
        // Add user as collaborator if UserGithubName is configured
        if (!string.IsNullOrEmpty(userGithubName))
        {
            // Validate GitHub username (alphanumeric and hyphens only)
            if (!System.Text.RegularExpressions.Regex.IsMatch(userGithubName, @"^[a-zA-Z0-9-]+$"))
            {
                await LogToDb(taskId, $"Invalid GitHub username: {userGithubName}");
                await SendSlackMessage(slack, $"⚠️ Invalid UserGithubName configured: {userGithubName}");
            }
            else
            {
                await LogToDb(taskId, $"Adding {userGithubName} as collaborator");
                
                // Get the current user's login first to avoid nested shell substitution
                var currentUserOutput = await RunCommand("gh api user --jq .login");
                var currentUser = currentUserOutput.Trim();
                
                if (string.IsNullOrEmpty(currentUser) || currentUser.Contains("error"))
                {
                    await LogToDb(taskId, $"Could not determine current GitHub user");
                    await SendSlackMessage(slack, $"⚠️ Could not add collaborator - authentication issue");
                }
                else
                {
                    var addCollabOutput = await RunCommand($"gh api repos/{currentUser}/{repoName}/collaborators/{userGithubName} -X PUT -f permission=push");
                    
                    if (addCollabOutput.Contains("error") || addCollabOutput.Contains("Not Found"))
                    {
                        await LogToDb(taskId, $"Warning: Could not add {userGithubName} as collaborator: {addCollabOutput}");
                        await SendSlackMessage(slack, $"⚠️ Could not add {userGithubName} as collaborator - please add manually");
                    }
                    else
                    {
                        await SendSlackMessage(slack, $"✅ Added {userGithubName} as collaborator to {repoName}");
                    }
                }
            }
        }
        
        // Get the repo URL
        var repoUrlOutput = await RunCommand($"gh repo view {repoName} --json url --jq .url");
        var repoUrl = repoUrlOutput.Trim();
        
        await LogToDb(taskId, $"Created repository: {repoUrl}");
        return repoUrl;
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error creating repository: {ex.Message}");
        return null;
    }
}

async Task DeliverNonCodingTask(int taskId, string taskFolder, ISlackApiClient slack)
{
    try
    {
        // Get all files from the task folder
        var files = Directory.GetFiles(taskFolder, "*", SearchOption.AllDirectories);
        
        if (files.Length == 0)
        {
            await SendSlackMessage(slack, $"✅ Task #{taskId} completed - no deliverables to return");
            return;
        }
        
        await SendSlackMessage(slack, $"📦 Delivering results for task #{taskId}...");
        
        var failedUploads = new List<string>();
        
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            var relativePath = Path.GetRelativePath(taskFolder, file);
            
            // For text files, send content directly if small enough, otherwise try to upload
            if (fileInfo.Extension.ToLower() is ".txt" or ".md" or ".json" or ".csv" or ".log" or ".html" or ".xml" or ".yaml" or ".yml")
            {
                var content = await System.IO.File.ReadAllTextAsync(file);
                
                // If content is small enough, send inline
                if (content.Length <= 3000)
                {
                    await SendSlackMessage(slack, $"📄 **{relativePath}**\n```\n{content}\n```");
                }
                else
                {
                    // Try to upload as file to Slack
                    var uploaded = await TryUploadFileToSlack(slack, file, relativePath, taskId);
                    if (!uploaded)
                    {
                        failedUploads.Add(relativePath);
                    }
                }
            }
            else
            {
                // For binary files, try to upload to Slack
                var uploaded = await TryUploadFileToSlack(slack, file, relativePath, taskId);
                if (!uploaded)
                {
                    failedUploads.Add(relativePath);
                }
            }
        }
        
        // If any files failed to upload, create a GitHub repo
        if (failedUploads.Count > 0)
        {
            await SendSlackMessage(slack, $"⚠️ Some files could not be delivered via Slack. Creating a GitHub repository...");
            await LogToDb(taskId, $"Failed to upload {failedUploads.Count} files to Slack, creating GitHub repo");
            
            var repoUrl = await CreateRepoForDeliverables(taskId, taskFolder, slack);
            
            if (!string.IsNullOrEmpty(repoUrl))
            {
                await SendSlackMessage(slack, $"✅ Files delivered via GitHub repository: {repoUrl}");
                if (!string.IsNullOrEmpty(userGithubName))
                {
                    await SendSlackMessage(slack, $"👤 User {userGithubName} has been added as a collaborator");
                }
                // Clean up the task folder only after successful delivery
                Directory.Delete(taskFolder, true);
                await LogToDb(taskId, $"Cleaned up task folder: {taskFolder}");
            }
            else
            {
                await SendSlackMessage(slack, $"❌ Could not deliver files via Slack or GitHub. Files have been preserved on the agent host for manual retrieval.");
                await LogToDb(taskId, $"Warning: Files preserved at {taskFolder} due to delivery failure");
                // Do NOT delete taskFolder - keep files for manual retrieval
            }
        }
        else
        {
            await SendSlackMessage(slack, $"✅ All deliverables for task #{taskId} have been sent");
            // Clean up the task folder only after successful delivery
            Directory.Delete(taskFolder, true);
            await LogToDb(taskId, $"Cleaned up task folder: {taskFolder}");
        }
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error delivering non-coding task: {ex.Message}");
    }
}

async Task<bool> TryUploadFileToSlack(ISlackApiClient slack, string filePath, string fileName, int taskId)
{
    try
    {
        await LogToDb(taskId, $"Attempting to upload file to Slack: {fileName}");
        
        // Stream file directly from disk to avoid loading entire file into memory
        using var fileStream = new System.IO.FileStream(
            filePath,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );
        
        // Use positional parameters for the Upload method
        var uploadResponse = await slack.Files.Upload(
            fileStream,           // Stream content
            fileName,            // filename
            null,                // filetype
            null,                // title (we'll set it separately if possible)
            null,                // initialComment
            null,                // threadTs
            new[] { agentChannelId }  // channels
        );
        
        if (uploadResponse != null && uploadResponse.File != null)
        {
            await SendSlackMessage(slack, $"📎 Uploaded: {fileName}");
            await LogToDb(taskId, $"Successfully uploaded {fileName} to Slack");
            return true;
        }
        
        return false;
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Failed to upload {fileName} to Slack: {ex.Message}");
        return false;
    }
}

async Task<string?> CreateRepoForDeliverables(int taskId, string taskFolder, ISlackApiClient slack)
{
    try
    {
        var sanitized = $"deliverables-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var repoName = $"luna-task-{taskId}-{sanitized}".ToLower();
        
        // Validate repo name
        if (!System.Text.RegularExpressions.Regex.IsMatch(repoName, @"^[a-z0-9-]+$"))
        {
            await LogToDb(taskId, $"Invalid repository name generated: {repoName}");
            repoName = $"luna-task-{taskId}";
        }
        
        // Check if GH_TOKEN is available
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(ghToken))
        {
            await LogToDb(taskId, "GH_TOKEN not available - cannot create repository");
            return null;
        }
        
        // Create repo
        await LogToDb(taskId, $"Creating GitHub repository for deliverables: {repoName}");
        var output = await RunCommand($"gh repo create {repoName} --private --confirm");
        
        if (output.Contains("error") || output.Contains("failed"))
        {
            await LogToDb(taskId, $"Failed to create repository: {output}");
            return null;
        }
        
        // Clone the repo
        var repoPath = Path.Combine("/tmp", $"luna-deliverables-{taskId}");
        var repoUrlOutput = await RunCommand($"gh repo view {repoName} --json sshUrl --jq .sshUrl");
        var repoUrl = repoUrlOutput.Trim();
        
        if (string.IsNullOrEmpty(repoUrl) || repoUrl.Contains("error"))
        {
            await LogToDb(taskId, "Could not get repository URL");
            return null;
        }
        
        var cloneOutput = await RunCommand($"git clone {repoUrl} {repoPath}");
        var gitDirPath = Path.Combine(repoPath, ".git");
        
        // Validate that clone succeeded
        if (cloneOutput.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            cloneOutput.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            cloneOutput.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(gitDirPath))
        {
            await LogToDb(taskId, $"Failed to clone repository to {repoPath}: {cloneOutput}");
            return null;
        }
        
        // Copy all files from task folder to repo
        foreach (var file in Directory.GetFiles(taskFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(taskFolder, file);
            var destPath = Path.Combine(repoPath, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            System.IO.File.Copy(file, destPath, true);
        }
        
        // Configure git identity for commits
        await RunCommand("git config user.name \"LUNA Agent\"", repoPath);
        await RunCommand("git config user.email \"luna-agent@localhost\"", repoPath);
        
        // Commit and push
        await RunCommand("git add .", repoPath);
        var commitMessage = $"LUNA Agent - Task #{taskId} deliverables";
        var commitOutput = await RunCommand($"git commit -m \"{commitMessage}\"", repoPath);
        
        // Check for commit errors
        if (commitOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
            commitOutput.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase))
        {
            await LogToDb(taskId, $"Git commit failed: no changes to commit");
            return null;
        }
        
        var defaultBranch = await RunCommand("git symbolic-ref --short HEAD", repoPath);
        defaultBranch = defaultBranch.Trim();
        if (string.IsNullOrWhiteSpace(defaultBranch))
            defaultBranch = "main";
        
        var pushOutput = await RunCommand($"git push origin {defaultBranch}", repoPath);
        
        // Check for push errors
        if (pushOutput.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            pushOutput.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            await LogToDb(taskId, $"Git push failed: {pushOutput}");
            return null;
        }
        
        // Add user as collaborator if configured
        if (!string.IsNullOrEmpty(userGithubName))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(userGithubName, @"^[a-zA-Z0-9-]+$"))
            {
                var currentUserOutput = await RunCommand("gh api user --jq .login");
                var currentUser = currentUserOutput.Trim();
                
                if (!string.IsNullOrEmpty(currentUser) && !currentUser.Contains("error"))
                {
                    await RunCommand($"gh api repos/{currentUser}/{repoName}/collaborators/{userGithubName} -X PUT -f permission=push");
                }
            }
        }
        
        // Get web URL
        var webUrlOutput = await RunCommand($"gh repo view {repoName} --json url --jq .url");
        var webUrl = webUrlOutput.Trim();
        
        // Cleanup temp repo
        if (Directory.Exists(repoPath))
            Directory.Delete(repoPath, true);
        
        return webUrl;
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error creating repository for deliverables: {ex.Message}");
        return null;
    }
}

// ============================================================================
// Agentic Task Processing
// ============================================================================

async Task ProcessTask(WorkTask task, ISlackApiClient slack)
{
    string? containerId = null;
    try
    {
        await UpdateTaskStatus(task.Id, TaskStatus.Running);
        await SendSlackMessage(slack, $"🚀 Starting task #{task.Id}: {task.Description}");
        await LogToDb(task.Id, $"Task started: {task.Description}");
        await LogThought(task.Id, 0, ThoughtType.UserUpdate, $"Task started: {task.Description}");

        // Create Docker container for this task
        var (success, containerIdResult, output) = await CreateTaskContainer(task.Id, task.Description);
        if (!success)
        {
            await SendSlackMessage(slack, $"❌ Failed to create container for task #{task.Id}: {output}");
            await LogThought(task.Id, 0, ThoughtType.Error, $"Container creation failed: {output}");
            await UpdateTaskStatus(task.Id, TaskStatus.Failed, errorMessage: "Failed to create container");
            return;
        }
        
        containerId = containerIdResult;
        await SendSlackMessage(slack, $"🐳 Created isolated container for task #{task.Id}");
        await LogThought(task.Id, 0, ThoughtType.Observation, "Docker container created successfully");

        // ============================================================================
        // INITIALIZER STEP: Create multi-step plan for the task
        // ============================================================================
        await SendSlackMessage(slack, $"📋 **Creating execution plan for task #{task.Id}...**");
        await LogThought(task.Id, 0, ThoughtType.Planning, "Running initializer: creating multi-step execution plan");
        
        var initializerPrompt = $@"You are LUNA, an AI agent. Review this task and create a detailed multi-step execution plan.

Task: {task.Description}

Create a numbered list of specific steps to complete this task. Each step should be completable in one iteration.
If a step fails, the plan should allow for revisiting that step in the next iteration.

Respond with ONLY a JSON array of step descriptions (no markdown, no code blocks):
[
  ""Step 1 description"",
  ""Step 2 description"",
  ...
]";
        
        var planResponse = await CallOllama(initializerPrompt);
        var executionPlan = ""; // Store plan for context
        if (!string.IsNullOrEmpty(planResponse))
        {
            try
            {
                // Clean up markdown if present
                var cleanedPlan = CleanMarkdownFromResponse(planResponse);
                
                var planArray = JsonDocument.Parse(cleanedPlan).RootElement;
                var planSteps = new StringBuilder("📋 **Execution Plan Created:**\n");
                int stepNum = 1;
                foreach (var step in planArray.EnumerateArray())
                {
                    planSteps.AppendLine($"{stepNum}. {step.GetString()}");
                    stepNum++;
                }
                
                executionPlan = planSteps.ToString();
                await SendSlackMessage(slack, executionPlan);
                await LogThought(task.Id, 0, ThoughtType.Planning, executionPlan);
            }
            catch (Exception ex)
            {
                await LogThought(task.Id, 0, ThoughtType.Error, $"Failed to parse plan: {ex.Message}. Continuing without formal plan.");
                await SendSlackMessage(slack, "⚠️ Could not create structured plan, proceeding with task...");
            }
        }
        else
        {
            await SendSlackMessage(slack, "⚠️ Initializer did not respond, proceeding with task...");
        }

        // Agentic loop with iteration
        var iteration = 0;
        var completed = false;
        var workingDir = "/workspace";
        var contextHistory = new StringBuilder();
        // Track consecutive empty AI responses across iterations
        // Note: CallOllama has its own retry logic (up to MaxOllamaRetries retries per call)
        // If CallOllama exhausts retries and returns empty, ollamaFailureCount increments
        // After MaxOllamaRetries consecutive empty responses (across iterations), pause task
        var ollamaFailureCount = 0;
        
        // Add execution plan to initial context if available
        if (!string.IsNullOrEmpty(executionPlan))
        {
            contextHistory.AppendLine(executionPlan);
        }

        while (!completed && iteration < MaxTaskIterations && task.Status != TaskStatus.Stopped)
        {
            iteration++;
            var iterationStartTime = DateTime.UtcNow;
            
            // Check for pause/stop at start of each iteration
            using (var db = new AgentDbContext())
            {
                var currentTaskStatus = await db.Tasks.FindAsync(task.Id);
                if (currentTaskStatus?.Status == TaskStatus.Stopped)
                {
                    await SendSlackMessage(slack, $"🛑 Task #{task.Id} stopped by user");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Task stopped by user");
                    break;
                }
                else if (currentTaskStatus?.Status == TaskStatus.Paused)
                {
                    await SendSlackMessage(slack, $"⏸️ Task #{task.Id} paused by user");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Task paused by user");
                    return;
                }
            }
            
            var iterationMsg = $"🔄 **Iteration {iteration}/{MaxTaskIterations}** for task #{task.Id}";
            await SendSlackMessage(slack, iterationMsg);
            await LogToDb(task.Id, $"Iteration {iteration} started");
            await LogThought(task.Id, iteration, ThoughtType.Observation, $"Starting iteration {iteration}");
            
            // Check if iteration has already exceeded time limit before making potentially slow AI call
            // Note: This mainly applies to iterations where previous operations (commands, file operations) 
            // consumed most of the time budget. First iteration will always pass this check.
            var iterationElapsed = (DateTime.UtcNow - iterationStartTime).TotalSeconds;
            if (iterationElapsed > MaxIterationTimeSeconds)
            {
                await SendSlackMessage(slack, $"⏱️ Iteration {iteration} reached time limit ({MaxIterationTimeSeconds}s) before AI call. Progress recorded, continuing in next iteration...");
                await LogThought(task.Id, iteration, ThoughtType.Observation, $"Iteration reached time limit before AI call. Elapsed: {iterationElapsed:F0}s");
                contextHistory.AppendLine($"[Iteration {iteration}] Time limit reached ({iterationElapsed:F0}s) before AI call. Continuing in next iteration.");
                await Task.Delay(IterationContinueDelayMs);
                continue;
            }

            // Build context-aware prompt with history from previous iterations
            var prompt = $@"You are LUNA, an AI agent running in an isolated Docker container. Current task: {task.Description}
Iteration: {iteration}
Working directory: {workingDir}

You can run commands in the container, create files, and use installed tools (git, curl, wget, build-essential).
You can use curl/wget to fetch data from the web, search for information, or download files.
For web research, you can use the 'research' action or directly use curl/wget commands.

IMPORTANT: 
- Respond with ONLY valid JSON. Do NOT wrap your response in markdown code blocks or backticks.
- Do NOT ask follow-up questions - proceed with the task autonomously.
- If the task is complete, use action ""complete"".
- Only use action ""need_input"" if you CANNOT proceed without critical information from the user.
- To write or create files with any text content, ALWAYS use the ""create_file"" action. Do NOT use shell commands like echo, printf, cat, or heredocs to write file content — these fail when the text contains apostrophes, quotes, or other special characters.
- For ""create_file"", set ""file_path"" to a path relative to /workspace (e.g. ""essay.txt"" or ""src/main.py""), NOT an absolute path like ""/workspace/essay.txt"".
- If a previous iteration's command failed with an error, do NOT repeat the same command. Analyze the error and use a different approach (e.g. switch from a shell command to the ""create_file"" action, or adjust the command to avoid the issue).

{(contextHistory.Length > 0 ? $@"
Previous iteration context:
{contextHistory}

Based on the above context, what is the next step?" : "What is the next step to complete this task? Provide a specific, executable action.")}

Respond with ONLY this JSON format (no markdown, no code blocks):
{{
  ""action"": ""command"" or ""create_file"" or ""research"" or ""complete"" or ""need_input"",
  ""details"": ""specific details"",
  ""command"": ""bash command if action is command"",
  ""file_path"": ""path relative to /workspace if action is create_file"",
  ""file_content"": ""content if action is create_file"",
  ""question"": ""question if action is need_input""
}}";

            // Show thinking indicator to user
            await SendSlackMessage(slack, $"🤔 AI is analyzing the task and determining next action...");
            
            var aiResponse = await CallOllama(prompt);
            await LogToDb(task.Id, $"AI response: {aiResponse}");
            await LogThought(task.Id, iteration, ThoughtType.AIResponse, aiResponse);

            if (string.IsNullOrEmpty(aiResponse))
            {
                ollamaFailureCount++;
                await SendSlackMessage(slack, $"⚠️ AI did not respond for task #{task.Id} (failure {ollamaFailureCount})");
                await LogThought(task.Id, iteration, ThoughtType.Error, $"AI did not respond (failure {ollamaFailureCount})");
                contextHistory.AppendLine($"[Iteration {iteration}] ERROR: AI did not respond");
                
                // After multiple consecutive failures, pause the task and try next one
                if (ollamaFailureCount >= MaxOllamaRetries)
                {
                    await SendSlackMessage(slack, $"⏸️ Task #{task.Id} paused due to repeated AI failures. Will try next task in queue.");
                    await LogThought(task.Id, iteration, ThoughtType.Error, "Task paused due to repeated AI failures");
                    await UpdateTaskStatus(task.Id, TaskStatus.Paused, errorMessage: $"Paused after {ollamaFailureCount} consecutive AI failures");
                    return; // Exit and allow next task to run
                }
                
                await Task.Delay(OllamaFailureDelayMs);
                continue;
            }

            // Strip markdown code blocks if present (common AI wrapping pattern)
            var cleanedResponse = CleanMarkdownFromResponse(aiResponse);

            // Parse AI response
            try
            {
                var actionDoc = JsonDocument.Parse(cleanedResponse);
                var action = actionDoc.RootElement.GetProperty("action").GetString();
                
                // Reset failure count only after successful parse
                ollamaFailureCount = 0;

                if (action == "complete" || aiResponse.Contains("TASK_COMPLETE"))
                {
                    completed = true;
                    await SendSlackMessage(slack, $"✅ **Task #{task.Id} completed!**\n🎯 AI has determined all objectives are met.");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Task completed successfully");
                    await UpdateTaskStatus(task.Id, TaskStatus.Completed, result: "Task completed successfully");
                    break;
                }
                else if (action == "need_input")
                {
                    var question = actionDoc.RootElement.GetProperty("question").GetString() ?? "Need more information";
                    await SendSlackMessage(slack, $"❓ **Task #{task.Id} needs input:**\n{question}\n\n💡 Use `!prompt {task.Id} <your response>` to provide the information and resume the task.");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Needs input: {question}");
                    await UpdateTaskStatus(task.Id, TaskStatus.Paused);
                    await LogToDb(task.Id, $"Waiting for user input: {question}");
                    return; // Exit and wait for user response
                }
                else if (action == "command")
                {
                    var command = actionDoc.RootElement.GetProperty("command").GetString() ?? "";
                    var details = actionDoc.RootElement.TryGetProperty("details", out var detailsElement) 
                        ? detailsElement.GetString() : "";
                    
                    if (!string.IsNullOrEmpty(details))
                    {
                        await SendSlackMessage(slack, $"💭 **AI Reasoning:** {details}");
                        contextHistory.AppendLine($"[Iteration {iteration}] Thought: {details}");
                    }
                    
                    await SendSlackMessage(slack, $"💻 **Executing command:**\n```{command}```");
                    await LogThought(task.Id, iteration, ThoughtType.Action, command, "command", command);
                    
                    var commandOutput = await RunCommandInContainer(containerId!, command);
                    await LogToDb(task.Id, $"Command output: {commandOutput}");
                    await LogThought(task.Id, iteration, ThoughtType.CommandOutput, commandOutput);
                    
                    // Show command output
                    await SendSlackMessage(slack, FormatOutput(commandOutput, "Output"));
                    
                    // Add to context history for next iteration
                    contextHistory.AppendLine($"[Iteration {iteration}] Executed: {command}");
                    contextHistory.AppendLine($"Output: {commandOutput.Substring(0, Math.Min(MaxContextHistoryEntryLength, commandOutput.Length))}");
                    if (commandOutput.Contains("Error (exit") || commandOutput.StartsWith("Exception"))
                    {
                        contextHistory.AppendLine($"[Iteration {iteration}] COMMAND FAILED — do NOT repeat this command. Use a different approach (e.g. use the 'create_file' action instead of shell commands for writing file content).");
                    }
                }
                else if (action == "create_file")
                {
                    var filePath = actionDoc.RootElement.GetProperty("file_path").GetString() ?? "";
                    var fileContent = actionDoc.RootElement.GetProperty("file_content").GetString() ?? "";
                    var details = actionDoc.RootElement.TryGetProperty("details", out var detailsElement) 
                        ? detailsElement.GetString() : "";
                    
                    if (!string.IsNullOrEmpty(details))
                    {
                        await SendSlackMessage(slack, $"💭 **AI Reasoning:** {details}");
                        contextHistory.AppendLine($"[Iteration {iteration}] Thought: {details}");
                    }
                    
                    // Normalize the file path: strip any leading workingDir or / prefix so it is always relative
                    if (filePath.StartsWith($"{workingDir}/"))
                        filePath = filePath.Substring($"{workingDir}/".Length);
                    else if (filePath.StartsWith("/"))
                        filePath = filePath.TrimStart('/');

                    // Create file in container
                    var tempFile = Path.Combine("/tmp", $"luna-temp-{task.Id}-{Path.GetFileName(filePath)}");
                    await System.IO.File.WriteAllTextAsync(tempFile, fileContent);
                    await CopyFileToContainer(containerId!, tempFile, $"{workingDir}/{filePath}");
                    System.IO.File.Delete(tempFile);
                    
                    await SendSlackMessage(slack, $"📄 **Created file:** `{filePath}`\n_{fileContent.Length} characters_");
                    await LogToDb(task.Id, $"Created file: {filePath}");
                    await LogThought(task.Id, iteration, ThoughtType.Action, $"Created file: {filePath}", "create_file", filePath);
                    
                    // Add to context history
                    contextHistory.AppendLine($"[Iteration {iteration}] Created file: {workingDir}/{filePath}");
                }
                else if (action == "research")
                {
                    var details = actionDoc.RootElement.GetProperty("details").GetString() ?? "";
                    await SendSlackMessage(slack, $"🔍 **Researching:** {details}");
                    await LogToDb(task.Id, $"Research: {details}");
                    await LogThought(task.Id, iteration, ThoughtType.Planning, $"Research: {details}");
                    
                    var researchResult = await DoOnlineResearch(details);
                    await LogToDb(task.Id, $"Research results: {researchResult}");
                    await LogThought(task.Id, iteration, ThoughtType.Observation, researchResult);
                    
                    // Format research results without code block (plain text is more readable)
                    await SendSlackMessage(slack, FormatOutput(researchResult, "Research Results", useCodeBlock: false));
                    
                    // Add to context history
                    contextHistory.AppendLine($"[Iteration {iteration}] Research: {details}");
                    contextHistory.AppendLine($"Results: {researchResult.Substring(0, Math.Min(MaxContextHistoryEntryLength, researchResult.Length))}");
                }
            }
            catch (Exception ex)
            {
                var responsePreview = cleanedResponse.Length > MaxErrorMessagePreviewLength 
                    ? cleanedResponse.Substring(0, MaxErrorMessagePreviewLength) 
                    : cleanedResponse;
                var errorMsg = $"Error parsing AI response: {ex.Message}. Raw response: {responsePreview}";
                await LogToDb(task.Id, errorMsg);
                await LogThought(task.Id, iteration, ThoughtType.Error, $"Error: {ex.Message}");
                await SendSlackMessage(slack, $"⚠️ Error processing AI response: {ex.Message}");
                
                // Add error to context so AI knows what went wrong
                contextHistory.AppendLine($"[Iteration {iteration}] ERROR: Failed to parse JSON response. {ex.Message}");
                contextHistory.AppendLine($"You must respond with valid JSON only, without markdown code blocks.");
            }

            await Task.Delay(2000); // Rate limiting
        }

        // If task is completed, handle deliverables based on task type
        if (completed)
        {
            // Create task-specific folder in home directory (sibling to luna execution directory)
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var taskFolder = Path.Combine(homeDir, $"luna-task-{task.Id}");
            Directory.CreateDirectory(taskFolder);
            
            // Copy files from container to task folder
            await LogToDb(task.Id, $"Copying deliverables to {taskFolder}");
            await CopyFileFromContainer(containerId!, "/workspace/.", taskFolder);
            
            var hasFiles = Directory.GetFiles(taskFolder, "*", SearchOption.AllDirectories).Length > 0;
            
            if (!hasFiles)
            {
                // No files created, just mark complete
                await SendSlackMessage(slack, $"✅ Task #{task.Id} completed with no deliverables");
                Directory.Delete(taskFolder, true);
            }
            else
            {
                // Use AI to classify the task
                var classification = await ClassifyTaskWithAI(task.Description, task.Id);
                
                if (!classification.IsCodeTask)
                {
                    // Non-coding task: deliver results via Slack and cleanup
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Delivering non-coding task results");
                    await DeliverNonCodingTask(task.Id, taskFolder, slack);
                }
                else
                {
                    // Coding task: check for repo reference
                    if (classification.RequiresExternalRepoAccess && !string.IsNullOrEmpty(classification.UrlOfExternalGitHubRepo))
                    {
                        // Existing repo referenced - clone/pull and create PR
                        await SendSlackMessage(slack, $"📦 Working with repository: {classification.UrlOfExternalGitHubRepo}");
                        await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Processing repo: {classification.UrlOfExternalGitHubRepo}");
                        
                        var repoPath = Path.Combine("/tmp", $"luna-repo-{task.Id}");
                        var repoAccessible = await CloneOrPullRepo(classification.UrlOfExternalGitHubRepo, repoPath, task.Id);
                        
                        if (!repoAccessible)
                        {
                            // Cannot access repo - mark task as failed
                            await UpdateTaskStatus(task.Id, TaskStatus.Failed, errorMessage: $"Unable to access repository: {classification.UrlOfExternalGitHubRepo}");
                            await SendSlackMessage(slack, $"❌ Task #{task.Id} failed: Cannot access repository {classification.UrlOfExternalGitHubRepo}. Please grant the agent access to this repository.");
                            await LogThought(task.Id, iteration, ThoughtType.Error, $"Cannot access repo: {classification.UrlOfExternalGitHubRepo}");
                            
                            // Cleanup
                            Directory.Delete(taskFolder, true);
                            if (Directory.Exists(repoPath))
                                Directory.Delete(repoPath, true);
                            
                            return;
                        }
                        
                        // Copy files from task folder to repo
                        var sanitized = new string(task.Description.Take(30).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
                        var branchName = $"luna-task-{task.Id}-{sanitized}".ToLower();
                        
                        await RunCommand($"git checkout -b {branchName}", repoPath);
                        
                        foreach (var file in Directory.GetFiles(taskFolder, "*", SearchOption.AllDirectories))
                        {
                            var relativePath = Path.GetRelativePath(taskFolder, file);
                            var destPath = Path.Combine(repoPath, relativePath);
                            var destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);
                            System.IO.File.Copy(file, destPath, true);
                        }
                        
                        // Create PR
                        var prUrl = await CreatePullRequest(task.Id, branchName, repoPath, task.Description);
                        
                        if (!string.IsNullOrEmpty(prUrl))
                        {
                            using var db = new AgentDbContext();
                            var dbTask = await db.Tasks.FindAsync(task.Id);
                            if (dbTask != null)
                            {
                                dbTask.BranchName = branchName;
                                dbTask.PullRequestUrl = prUrl;
                                await db.SaveChangesAsync();
                            }
                            await SendSlackMessage(slack, $"✅ Pull request created: {prUrl}");
                            await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"PR created: {prUrl}");
                        }
                        
                        // Cleanup task folder and temp repo
                        Directory.Delete(taskFolder, true);
                        if (Directory.Exists(repoPath))
                            Directory.Delete(repoPath, true);
                    }
                    else
                    {
                        // No repo referenced - create new repo and add user as collaborator
                        await SendSlackMessage(slack, $"📦 Creating new repository for task #{task.Id}...");
                        await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Creating new repository");
                        
                        var repoUrl = await CreateNewGithubRepo(task.Id, task.Description, slack);
                        
                        if (string.IsNullOrEmpty(repoUrl))
                        {
                            await SendSlackMessage(slack, $"⚠️ Could not create repository, but task is complete. Files saved on the agent host for manual retrieval.");
                            await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Files saved locally at {taskFolder}");
                        }
                        else
                        {
                            // Clone the newly created repo
                            var repoPath = Path.Combine("/tmp", $"luna-repo-{task.Id}");
                            var cloneOutput = await RunCommand($"git clone {repoUrl} {repoPath}");
                            var gitDir = Path.Combine(repoPath, ".git");

                            // Ensure clone succeeded before proceeding
                            if (!Directory.Exists(gitDir) || cloneOutput.Contains("fatal") || cloneOutput.Contains("error"))
                            {
                                await LogToDb(task.Id, $"Warning: Failed to clone repository {repoUrl}: {cloneOutput}");
                                await SendSlackMessage(slack, $"⚠️ Repository {repoUrl} was created, but the agent could not clone it. Files saved on the agent host for manual retrieval.");
                                await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Repository created but clone failed; files kept locally at {taskFolder}");
                            }
                            else
                            {
                                // Copy files to repo
                                foreach (var file in Directory.GetFiles(taskFolder, "*", SearchOption.AllDirectories))
                                {
                                    var relativePath = Path.GetRelativePath(taskFolder, file);
                                    var destPath = Path.Combine(repoPath, relativePath);
                                    var destDir = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(destDir))
                                        Directory.CreateDirectory(destDir);
                                    System.IO.File.Copy(file, destPath, true);
                                }
                                
                                // Configure git identity
                                await RunCommand("git config user.name \"LUNA Agent\"", repoPath);
                                await RunCommand("git config user.email \"luna-agent@localhost\"", repoPath);
                                
                                // Commit and push to main
                                await RunCommand("git add .", repoPath);
                                var sanitizedDescription = task.Description
                                    .Replace("\"", "\\\"")
                                    .Replace("$", "\\$")
                                    .Replace("`", "\\`")
                                    .Replace("\n", " ")
                                    .Replace("\r", "")
                                    .Replace("\\", "\\\\");
                                var commitMessage = $"LUNA Agent - Task #{task.Id}: {sanitizedDescription}";
                                var commitOutput = await RunCommand($"git commit -m \"{commitMessage}\"", repoPath);
                                
                                // Check commit succeeded
                                if (commitOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                                {
                                    await LogToDb(task.Id, "Warning: No changes to commit");
                                }
                                
                                // Determine default branch and push
                                var defaultBranch = await RunCommand("git symbolic-ref --short HEAD", repoPath);
                                defaultBranch = defaultBranch.Trim();
                                if (string.IsNullOrWhiteSpace(defaultBranch))
                                    defaultBranch = "main";
                                
                                var pushOutput = await RunCommand($"git push origin {defaultBranch}", repoPath);
                                if (pushOutput.Contains("fatal") || pushOutput.Contains("error"))
                                {
                                    await LogToDb(task.Id, $"Warning: Failed to push to {defaultBranch}: {pushOutput}");
                                    await SendSlackMessage(slack, $"⚠️ Repository created but failed to push code. Repository: {repoUrl}");
                                }
                                else
                                {
                                    // Update task with repo URL
                                    using var db = new AgentDbContext();
                                    var dbTask = await db.Tasks.FindAsync(task.Id);
                                    if (dbTask != null)
                                    {
                                        dbTask.PullRequestUrl = repoUrl;
                                        await db.SaveChangesAsync();
                                    }
                                    
                                    await SendSlackMessage(slack, $"✅ Repository created and code pushed: {repoUrl}");
                                    if (!string.IsNullOrEmpty(userGithubName))
                                    {
                                        await SendSlackMessage(slack, $"👤 User {userGithubName} has been added as a collaborator");
                                    }
                                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Repository created: {repoUrl}");
                                }
                                
                                // Cleanup task folder and temp repo
                                Directory.Delete(taskFolder, true);
                                if (Directory.Exists(repoPath))
                                    Directory.Delete(repoPath, true);
                            }
                        }
                    }
                }
            }
        }

        if (!completed && iteration >= MaxTaskIterations)
        {
            await UpdateTaskStatus(task.Id, TaskStatus.Failed, errorMessage: "Max iterations reached");
            await SendSlackMessage(slack, $"❌ Task #{task.Id} failed: Max iterations reached");
            await LogThought(task.Id, iteration, ThoughtType.Error, "Max iterations reached");
        }
    }
    catch (Exception ex)
    {
        await UpdateTaskStatus(task.Id, TaskStatus.Failed, errorMessage: ex.Message);
        await SendSlackMessage(slack, $"❌ Task #{task.Id} failed: {ex.Message}");
        await LogToDb(task.Id, $"Error: {ex.Message}");
        await LogThought(task.Id, 0, ThoughtType.Error, $"Task failed: {ex.Message}");
    }
    finally
    {
        // Clean up container
        if (!string.IsNullOrEmpty(containerId))
        {
            await StopAndRemoveContainer(containerId);
            await SendSlackMessage(slack, $"🧹 Cleaned up container for task #{task.Id}");
            await LogThought(task.Id, 0, ThoughtType.Observation, "Container cleaned up");
        }
    }
}

// ============================================================================
// Task Worker
// ============================================================================

async Task TaskWorker(ISlackApiClient slack)
{
    while (true)
    {
        WorkTask? taskToProcess = null;

        lock (queueLock)
        {
            if (currentTask == null && taskQueue.Count > 0)
            {
                currentTask = taskQueue.Dequeue();
                taskToProcess = currentTask;
            }
        }

        if (taskToProcess != null)
        {
            await ProcessTask(taskToProcess, slack);

            lock (queueLock)
            {
                currentTask = null;
            }
        }

        await Task.Delay(1000);
    }
}

// ============================================================================
// Slack Event Handlers
// ============================================================================

async Task HandleSlackMessage(MessageEvent message, ISlackApiClient slack)
{
    try
    {
        if (message.Channel != agentChannelId || message.User == null)
            return;

        // Skip messages from the agent itself to prevent feedback loops
        if (message.User == agentUserId)
            return;

        var text = message.Text?.Trim() ?? "";

        // Parse commands (use ! prefix since Slack intercepts / as slash commands)
        if (text.StartsWith("!status"))
        {
            using var db = new AgentDbContext();
            var statusMsg = "📊 **LUNA Agent Status**\n\n";

            if (currentTask != null)
            {
                var ct = await db.Tasks.FindAsync(currentTask.Id);
                if (ct != null)
                {
                    var elapsed = DateTime.Now - (ct.StartedAt ?? ct.CreatedAt);
                    statusMsg += $"**Current Task:** #{ct.Id} - {ct.Description}\n";
                    statusMsg += $"**Status:** {ct.Status}\n";
                    statusMsg += $"**Duration:** {elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s\n\n";
                }
            }
            else
            {
                statusMsg += "**Current Task:** None\n\n";
            }

            var queuedTasks = db.Tasks.Where(t => t.Status == TaskStatus.Queued).ToList();
            statusMsg += $"**Queued Tasks:** {queuedTasks.Count}\n";
            foreach (var qt in queuedTasks.Take(5))
            {
                statusMsg += $"  • #{qt.Id}: {qt.Description}\n";
            }

            await SendSlackMessage(slack, statusMsg);
        }
        else if (text.StartsWith("!details"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var taskId))
            {
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null)
                {
                    var msg = $"📋 **Task #{task.Id} Details**\n\n";
                    msg += $"**Description:** {task.Description}\n";
                    msg += $"**Status:** {task.Status}\n";
                    msg += $"**Created:** {task.CreatedAt}\n";
                    if (task.StartedAt.HasValue)
                        msg += $"**Started:** {task.StartedAt.Value}\n";
                    if (task.CompletedAt.HasValue)
                        msg += $"**Completed:** {task.CompletedAt.Value}\n";
                    if (!string.IsNullOrEmpty(task.Result))
                        msg += $"**Result:** {task.Result}\n";
                    if (!string.IsNullOrEmpty(task.ErrorMessage))
                        msg += $"**Error:** {task.ErrorMessage}\n";
                    if (!string.IsNullOrEmpty(task.BranchName))
                        msg += $"**Branch:** {task.BranchName}\n";
                    if (!string.IsNullOrEmpty(task.PullRequestUrl))
                        msg += $"**PR:** {task.PullRequestUrl}\n";

                    await SendSlackMessage(slack, msg);

                    // Send log separately
                    if (!string.IsNullOrEmpty(task.Log))
                    {
                        var logMsg = $"**Log for Task #{task.Id}:**\n```{task.Log}```";
                        await SendSlackMessage(slack, logMsg);
                    }
                }
                else
                {
                    await SendSlackMessage(slack, $"Task #{taskId} not found");
                }
            }
        }
        else if (text.StartsWith("!pause"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var taskId))
            {
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null && (task.Status == TaskStatus.Queued || task.Status == TaskStatus.Running))
                {
                    await UpdateTaskStatus(taskId, TaskStatus.Paused);
                    
                    // Remove from queue if it's queued
                    lock (queueLock)
                    {
                        var queueList = taskQueue.ToList();
                        var wasInQueue = queueList.Any(t => t.Id == taskId);
                        if (wasInQueue)
                        {
                            queueList.RemoveAll(t => t.Id == taskId);
                            taskQueue.Clear();
                            foreach (var t in queueList)
                            {
                                taskQueue.Enqueue(t);
                            }
                        }
                    }
                    
                    await SendSlackMessage(slack, $"⏸️ Task #{taskId} paused");
                    
                    // If it's the current running task, log that it will pause at next iteration
                    if (currentTask?.Id == taskId)
                    {
                        await LogToDb(taskId, "Task will pause at next iteration check");
                        await SendSlackMessage(slack, $"ℹ️ Task #{taskId} will pause at the next iteration. Next queued task will start.");
                    }
                    else
                    {
                        await SendSlackMessage(slack, $"ℹ️ Task #{taskId} removed from queue. Next task will start if available.");
                    }
                }
                else
                {
                    await SendSlackMessage(slack, $"Cannot pause task #{taskId} - must be queued or running");
                }
            }
        }
        else if (text.StartsWith("!update"))
        {
            var parts = text.Split(' ', 3);
            if (parts.Length >= 3 && int.TryParse(parts[1], out var taskId))
            {
                var updateText = parts[2];
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null && (task.Status == TaskStatus.Running || task.Status == TaskStatus.Queued))
                {
                    // Append update to task description for AI context
                    task.Description += $"\n\nUser Update: {updateText}";
                    await db.SaveChangesAsync();
                    
                    // Log the update (iteration 0 indicates user action outside iteration loop)
                    await LogToDb(taskId, $"User update: {updateText}");
                    await LogThought(taskId, 0, ThoughtType.UserUpdate, $"User provided update: {updateText}");
                    
                    if (task.Status == TaskStatus.Running)
                    {
                        await SendSlackMessage(slack, $"✅ Update sent to task #{taskId}. AI will see this in the next iteration.");
                    }
                    else if (task.Status == TaskStatus.Queued)
                    {
                        await SendSlackMessage(slack, $"✅ Update sent to queued task #{taskId}. AI will see this when the task starts.");
                    }
                }
                else
                {
                    await SendSlackMessage(slack, $"Cannot update task #{taskId} - task not found or already completed");
                }
            }
            else
            {
                await SendSlackMessage(slack, "Usage: !update <task_id> <message>");
            }
        }
        else if (text.StartsWith("!prompt"))
        {
            var parts = text.Split(' ', 3);
            if (parts.Length >= 3 && int.TryParse(parts[1], out var taskId))
            {
                var promptText = parts[2];
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null && task.Status == TaskStatus.Paused)
                {
                    // Store prompt response on task (replaces any previous prompt)
                    task.UserPrompt = promptText;
                    
                    // Remove any previous prompt response from description and add new one
                    var descLines = task.Description.Split('\n');
                    var filteredDesc = string.Join('\n', descLines.Where(line => !line.StartsWith("User Prompt Response:")));
                    task.Description = filteredDesc.TrimEnd() + $"\n\nUser Prompt Response: {promptText}";
                    
                    await db.SaveChangesAsync();
                    
                    // Log the prompt
                    await LogToDb(taskId, $"User prompt response: {promptText}");
                    await LogThought(taskId, 0, ThoughtType.UserUpdate, $"User provided prompt response: {promptText}");
                    
                    // Resume task by adding to front of queue
                    lock (queueLock)
                    {
                        var queueList = taskQueue.ToList();
                        queueList.RemoveAll(t => t.Id == taskId);
                        taskQueue.Clear();
                        
                        // Add specified task at the front of queue
                        taskQueue.Enqueue(task);
                        
                        // Re-add other tasks after it
                        foreach (var t in queueList)
                        {
                            taskQueue.Enqueue(t);
                        }
                    }
                    
                    // Update task status to queued for auto-start
                    await UpdateTaskStatus(taskId, TaskStatus.Queued);
                    
                    await SendSlackMessage(slack, $"✅ Prompt response saved for task #{taskId}. Task resumed and moved to front of queue.");
                }
                else if (task == null)
                {
                    await SendSlackMessage(slack, $"Task #{taskId} not found");
                }
                else
                {
                    await SendSlackMessage(slack, $"Cannot prompt task #{taskId} - task is not paused (current status: {task.Status})");
                }
            }
            else
            {
                await SendSlackMessage(slack, "Usage: !prompt <task_id> <response>");
            }
        }
        else if (text.StartsWith("!start"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var taskId))
            {
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null)
                {
                    // If this task is already running, nothing to do
                    if (task.Status == TaskStatus.Running && currentTask?.Id == taskId)
                    {
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} is already running");
                        return;
                    }
                    
                    // If there's a different current task, pause it
                    if (currentTask != null && currentTask.Id != taskId)
                    {
                        await UpdateTaskStatus(currentTask.Id, TaskStatus.Paused);
                        await SendSlackMessage(slack, $"⏸️ Paused current task #{currentTask.Id}");
                        lock (queueLock)
                        {
                            currentTask = null;
                        }
                    }

                    // Remove task from queue if it's already there (we'll re-add it at front)
                    lock (queueLock)
                    {
                        var queueList = taskQueue.ToList();
                        queueList.RemoveAll(t => t.Id == taskId);
                        taskQueue.Clear();
                        
                        // Add specified task at the front of queue
                        taskQueue.Enqueue(task);
                        
                        // Re-add other tasks after it
                        foreach (var t in queueList)
                        {
                            taskQueue.Enqueue(t);
                        }
                    }
                    
                    // Store original status for message
                    var originalStatus = task.Status;
                    
                    // Update task status to queued
                    if (task.Status != TaskStatus.Queued)
                    {
                        await UpdateTaskStatus(taskId, TaskStatus.Queued);
                    }
                    
                    // Send appropriate message based on original status
                    if (originalStatus == TaskStatus.Paused)
                    {
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} resumed and moved to front of queue");
                    }
                    else
                    {
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} moved to front of queue and will start next");
                    }
                }
                else
                {
                    await SendSlackMessage(slack, $"Task #{taskId} not found");
                }
            }
        }
        else if (text.StartsWith("!stop"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var taskId))
            {
                await UpdateTaskStatus(taskId, TaskStatus.Stopped);
                await SendSlackMessage(slack, $"🛑 Task #{taskId} stopped");
            }
        }
        else if (text.StartsWith("!delete"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var taskId))
            {
                using var db = new AgentDbContext();
                var task = await db.Tasks.FindAsync(taskId);
                if (task != null)
                {
                    // If it's the current task, clear it from current
                    if (currentTask?.Id == taskId)
                    {
                        lock (queueLock)
                        {
                            currentTask = null;
                        }
                    }
                    
                    // Remove from queue if present
                    lock (queueLock)
                    {
                        var queueList = taskQueue.ToList();
                        queueList.RemoveAll(t => t.Id == taskId);
                        taskQueue.Clear();
                        foreach (var t in queueList)
                        {
                            taskQueue.Enqueue(t);
                        }
                    }
                    
                    // Delete from database (cascade will delete related thoughts)
                    db.Tasks.Remove(task);
                    await db.SaveChangesAsync();
                    
                    await SendSlackMessage(slack, $"🗑️ Task #{taskId} deleted");
                }
                else
                {
                    await SendSlackMessage(slack, $"Task #{taskId} not found");
                }
            }
            else
            {
                await SendSlackMessage(slack, "Usage: !delete <task_id>");
            }
        }
        else if (text.StartsWith("!queue"))
        {
            using var db = new AgentDbContext();
            var queuedTasks = db.Tasks
                .Where(t => t.Status == TaskStatus.Queued || t.Status == TaskStatus.Paused)
                .OrderBy(t => t.CreatedAt)
                .ToList();

            var msg = $"📋 **Task Queue** ({queuedTasks.Count} tasks)\n\n";
            foreach (var qt in queuedTasks)
            {
                msg += $"#{qt.Id} [{qt.Status}]: {qt.Description}\n";
            }

            await SendSlackMessage(slack, msg.Length > 0 ? msg : "Queue is empty");
        }
        else if (text.StartsWith("!help"))
        {
            var helpMsg = @"🤖 **LUNA Agent Commands**

**!status** - Show current task and queue
**!details <task_id>** - Get details about a specific task
**!queue** - Show all queued and paused tasks
**!pause <task_id>** - Pause a running or queued task
**!start <task_id>** - Start a task (pauses current task if needed, starts or resumes specified task)
**!stop <task_id>** - Stop a task
**!delete <task_id>** - Delete a task (can be used at any time, regardless of task state)
**!update <task_id> <message>** - Send additional context to a running task
**!prompt <task_id> <response>** - Provide prompt response to a paused task and resume it
**!system** - Get current system status (CPU, RAM, temperature, Ollama)
**!help** - Show this help message

**To create a new task**, simply send a message describing what you want me to do!";

            await SendSlackMessage(slack, helpMsg);
        }
        else if (text.StartsWith("!system"))
        {
            var (cpu, ram, temp) = await GetSystemStats();
            var (ollamaRunning, ollamaInfo) = await GetOllamaStatusAsync();
            var statusMessage = $"🖥️ *System Status*\n\n" +
                          $"CPU Usage: {cpu}%\n" +
                          $"RAM Usage: {ram}%\n" +
                          $"Temperature: {temp}°C\n" +
                          $"Ollama: {(ollamaRunning ? "✅" : "❌")} {ollamaInfo}";
            await SendSlackMessage(slack, statusMessage);
        }
        else if (!string.IsNullOrEmpty(text) && !text.StartsWith("!") && !text.StartsWith("/"))
        {
            // Create new task from message
            using var db = new AgentDbContext();
            var newTask = new WorkTask
            {
                Description = text,
                Status = TaskStatus.Queued,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                UserId = message.User
            };

            db.Tasks.Add(newTask);
            await db.SaveChangesAsync();

            lock (queueLock)
            {
                taskQueue.Enqueue(newTask);
            }

            await SendSlackMessage(slack, $"✅ Task #{newTask.Id} created and queued: {text}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error handling message: {ex.Message}");
    }
}

// ============================================================================
// Main Loop
// ============================================================================

Console.WriteLine("Connecting to Slack...");

var client = new SlackApiClient(slackBotToken);

// Get the agent's own bot user ID to filter out its own messages
var authTest = await client.Auth.Test();
agentUserId = authTest.UserId;
Console.WriteLine($"Agent User ID: {agentUserId}");

await SendSlackMessage(client, "🚀 LUNA Agent is online and ready!");

// Load any previously queued tasks from database into queue
Console.WriteLine("Loading queued tasks from database...");
using (var db = new AgentDbContext())
{
    var queuedTasks = db.Tasks
        .Where(t => t.Status == TaskStatus.Queued)
        .OrderBy(t => t.CreatedAt)
        .ToList();
    
    lock (queueLock)
    {
        foreach (var task in queuedTasks)
        {
            taskQueue.Enqueue(task);
        }
    }
    
    if (queuedTasks.Count > 0)
    {
        Console.WriteLine($"Loaded {queuedTasks.Count} queued tasks");
        await SendSlackMessage(client, $"📋 Loaded {queuedTasks.Count} queued task(s) from previous session");
    }
}

// Start task worker
var workerTask = Task.Run(async () => await TaskWorker(client));

// Start message polling for real-time message handling
Console.WriteLine("Setting up message polling...");
// Initialize to current time so we only process NEW messages (not historical)
var lastMessageTimestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var processedMessageTimestamps = new HashSet<string>();

var pollTask = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            try
            {
                // Poll for new messages in the agent channel
                if (!string.IsNullOrEmpty(agentChannelId))
                {
                    var history = await client.Conversations.History(agentChannelId);
                    
                    if (history.Messages != null && history.Messages.Any())
                    {
                        // Get timestamp from message (Slack uses Unix seconds as string)
                        var newMessages = history.Messages
                            .Where(m => {
                                // Skip system messages (no verbose logging for normal filtering)
                                if (string.IsNullOrWhiteSpace(m.Text))
                                    return false;
                                
                                if (m.Text.Contains("joined the channel") ||
                                    m.Text.Contains("LUNA Agent is online") ||
                                    m.Text.Contains("has joined"))
                                    return false;
                                
                                if (m.User == null)
                                    return false;
                                
                                // Convert DateTime to Unix timestamp for comparison
                                var msgTimestamp = new DateTimeOffset(m.Timestamp, TimeSpan.Zero).ToUnixTimeSeconds();
                                if (msgTimestamp <= lastMessageTimestamp)
                                    return false;
                                
                                if (processedMessageTimestamps.Contains(m.Timestamp.ToString("O")))
                                    return false;
                                
                                // Found a new message
                                return true;
                            })
                            .OrderBy(m => m.Timestamp)
                            .ToList();

                        // Only log when new messages are found (reduces console chattiness)
                        if (newMessages.Count > 0)
                            Console.WriteLine($"📨 Found {newMessages.Count} new message(s)");

                        foreach (var message in newMessages)
                        {
                            try
                            {
                                // Update lastMessageTimestamp for next poll cycle
                                var msgTimestamp = new DateTimeOffset(message.Timestamp, TimeSpan.Zero).ToUnixTimeSeconds();
                                lastMessageTimestamp = msgTimestamp;
                                
                                processedMessageTimestamps.Add(message.Timestamp.ToString("O"));

                                // Convert to MessageEvent and handle
                                if (message.User != null && !string.IsNullOrWhiteSpace(message.Text))
                                {
                                    var messageEvent = new MessageEvent
                                    {
                                        Channel = agentChannelId,
                                        User = message.User,
                                        Text = message.Text,
                                        Type = "message"
                                    };

                                    await HandleSlackMessage(messageEvent, client);
                                }
                            }
                            catch (Exception msgEx)
                            {
                                Console.WriteLine($"⚠️  Error processing message: {msgEx.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Error polling messages: {ex.Message}\n{ex.StackTrace}");
            }

            // Poll every 5 seconds
            await Task.Delay(5000);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Message polling thread error: {ex.Message}");
    }
});

Console.WriteLine("✅ LUNA Agent connected and listening via polling...");

// Keep the application running
await Task.Delay(Timeout.Infinite);

// ============================================================================
// Database Models & Context
// ============================================================================

public enum TaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Paused,
    Stopped
}

public enum ThoughtType
{
    Observation,
    Planning,
    Action,
    UserUpdate,
    AIResponse,
    CommandOutput,
    Error
}

public class WorkTask
{
    public int Id { get; set; }
    public string Description { get; set; } = "";
    public TaskStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BranchName { get; set; }
    public string? PullRequestUrl { get; set; }
    public string UserId { get; set; } = "";
    public string Log { get; set; } = "";
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? UserPrompt { get; set; }  // Stores the most recent prompt response for paused tasks
    
    // Navigation property for full agentic flow
    public List<AgenticThought> Thoughts { get; set; } = new();
}

public class AgenticThought
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int IterationNumber { get; set; }
    public ThoughtType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public string Content { get; set; } = "";
    public string? ActionType { get; set; }
    public string? ActionDetails { get; set; }
    public bool StreamedToUser { get; set; }
    
    // Navigation property
    public WorkTask Task { get; set; } = null!;
}

public class AgentDbContext : DbContext
{
    // Static property set once during initialization
    // Thread-safe as it's only written once before any reads
    public static string DbPath { get; set; } = "";
    public DbSet<WorkTask> Tasks { get; set; }
    public DbSet<AgenticThought> Thoughts { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var dbDir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);
        
        options.UseSqlite($"Data Source={DbPath}");
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkTask>()
            .HasMany(t => t.Thoughts)
            .WithOne(th => th.Task)
            .HasForeignKey(th => th.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskClassification
{
    public bool IsCodeTask { get; set; }
    public bool RequiresNewRepo { get; set; }
    public bool RequiresExternalRepoAccess { get; set; }
    public string? UrlOfExternalGitHubRepo { get; set; }
}
