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

// AI Configuration
const double OllamaTemperature = 0.7;
const int OllamaMaxTokens = 2048;
const string OllamaDefaultModel = "gemma3:4b";

// Task Processing Configuration
const int MaxTaskIterations = 100;
const int MaxSlackMessagePreviewLength = 500;
const int MaxDescriptionPreviewLength = 50;
const int MaxLogPreviewLength = 2000;

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

// ============================================================================
// Task Queue Management
// ============================================================================

var taskQueue = new Queue<WorkTask>();
WorkTask? currentTask = null;
var queueLock = new object();
var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromMinutes(5);

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

async Task<string> CallOllama(string prompt, string? model = null)
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
                return "";
            }
        }
        else
        {
            Console.WriteLine($"⚠️  Ollama HTTP error: {response.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ollama error: {ex.Message}");
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
        var containerName = $"luna-task-{taskId}";
        
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

async Task<(string? branchName, string? repoPath)> CreateGitBranch(int taskId, string taskDescription)
{
    try
    {
        var sanitized = new string(taskDescription.Take(30).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var branchName = $"luna-task-{taskId}-{sanitized}".ToLower();
        var repoPath = Path.Combine("/tmp", $"luna-repo-{taskId}");

        // Check if we're in a git repo or need to create a new one
        var gitConfigOutput = await RunCommand("git config --get remote.origin.url");
        
        if (string.IsNullOrEmpty(gitConfigOutput) || gitConfigOutput.Contains("Error"))
        {
            // Create a new repository
            Directory.CreateDirectory(repoPath);
            await RunCommand("git init", repoPath);
            await RunCommand("git config user.name 'LUNA Agent'", repoPath);
            await RunCommand("git config user.email 'luna-agent@localhost'", repoPath);
            
            // Try to get GitHub username from SSH config
            var githubUser = await RunCommand("git config --get user.name || echo 'luna-agent'");
            githubUser = githubUser.Trim();
            
            await LogToDb(taskId, $"Created new repository at {repoPath}");
        }
        else
        {
            // Use existing repository
            var currentDir = Directory.GetCurrentDirectory();
            repoPath = currentDir;
        }

        // Create and checkout branch
        await RunCommand($"git checkout -b {branchName}", repoPath);
        await LogToDb(taskId, $"Created branch: {branchName}");

        return (branchName, repoPath);
    }
    catch (Exception ex)
    {
        await LogToDb(taskId, $"Error creating branch: {ex.Message}");
        return (null, null);
    }
}

async Task<string?> CreatePullRequest(int taskId, string branchName, string repoPath, string taskDescription)
{
    try
    {
        // Commit changes
        await RunCommand("git add .", repoPath);
        var commitMessage = $"LUNA Agent - Task #{taskId}: {taskDescription.Substring(0, Math.Min(MaxDescriptionPreviewLength, taskDescription.Length))}";
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
Keep the response under 500 words.";

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

        // Agentic loop with iteration
        var iteration = 0;
        var completed = false;
        var workingDir = "/workspace";

        while (!completed && iteration < MaxTaskIterations && task.Status != TaskStatus.Stopped)
        {
            iteration++;
            var iterationMsg = $"⚙️ Iteration {iteration}/{MaxTaskIterations} for task #{task.Id}";
            await SendSlackMessage(slack, iterationMsg);
            await LogToDb(task.Id, $"Iteration {iteration} started");
            await LogThought(task.Id, iteration, ThoughtType.Observation, $"Starting iteration {iteration}");

            // Ask AI for next steps
            var prompt = $@"You are LUNA, an AI agent running in an isolated Docker container. Current task: {task.Description}
Iteration: {iteration}
Working directory: {workingDir}

You can run commands in the container, create files, and use installed tools (git, curl, wget, build-essential).
You can use curl/wget to fetch data from the web, search for information, or download files.
For web research, you can use the 'research' action or directly use curl/wget commands.
What is the next step to complete this task? Provide a specific, executable action.
If the task is complete, respond with 'TASK_COMPLETE'.
If you need user input, respond with 'NEED_INPUT: <question>'.

Respond in JSON format:
{{
  ""action"": ""command"" or ""create_file"" or ""research"" or ""complete"" or ""need_input"",
  ""details"": ""specific details"",
  ""command"": ""bash command if action is command"",
  ""file_path"": ""path if action is create_file"",
  ""file_content"": ""content if action is create_file"",
  ""question"": ""question if action is need_input""
}}";

            var aiResponse = await CallOllama(prompt);
            await LogToDb(task.Id, $"AI response: {aiResponse}");
            await LogThought(task.Id, iteration, ThoughtType.AIResponse, aiResponse);

            if (string.IsNullOrEmpty(aiResponse))
            {
                await SendSlackMessage(slack, $"⚠️ AI did not respond for task #{task.Id}");
                await LogThought(task.Id, iteration, ThoughtType.Error, "AI did not respond");
                await Task.Delay(5000);
                continue;
            }

            // Parse AI response
            try
            {
                var actionDoc = JsonDocument.Parse(aiResponse);
                var action = actionDoc.RootElement.GetProperty("action").GetString();

                if (action == "complete" || aiResponse.Contains("TASK_COMPLETE"))
                {
                    completed = true;
                    await SendSlackMessage(slack, $"✅ Task #{task.Id} completed!");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Task completed successfully");
                    await UpdateTaskStatus(task.Id, TaskStatus.Completed, result: "Task completed successfully");
                    break;
                }
                else if (action == "need_input")
                {
                    var question = actionDoc.RootElement.GetProperty("question").GetString() ?? "Need more information";
                    await SendSlackMessage(slack, $"❓ Task #{task.Id} needs input: {question}");
                    await LogThought(task.Id, iteration, ThoughtType.UserUpdate, $"Needs input: {question}");
                    await UpdateTaskStatus(task.Id, TaskStatus.Paused);
                    await LogToDb(task.Id, $"Waiting for user input: {question}");
                    return; // Exit and wait for user response
                }
                else if (action == "command")
                {
                    var command = actionDoc.RootElement.GetProperty("command").GetString() ?? "";
                    await SendSlackMessage(slack, $"💻 Running in container: `{command}`");
                    await LogThought(task.Id, iteration, ThoughtType.Action, command, "command", command);
                    
                    var commandOutput = await RunCommandInContainer(containerId!, command);
                    await LogToDb(task.Id, $"Command output: {commandOutput}");
                    await LogThought(task.Id, iteration, ThoughtType.CommandOutput, commandOutput);
                    await SendSlackMessage(slack, $"```{commandOutput.Substring(0, Math.Min(MaxSlackMessagePreviewLength, commandOutput.Length))}```");
                }
                else if (action == "create_file")
                {
                    var filePath = actionDoc.RootElement.GetProperty("file_path").GetString() ?? "";
                    var fileContent = actionDoc.RootElement.GetProperty("file_content").GetString() ?? "";
                    
                    // Create file in container
                    var tempFile = Path.Combine("/tmp", $"luna-temp-{task.Id}-{Path.GetFileName(filePath)}");
                    await System.IO.File.WriteAllTextAsync(tempFile, fileContent);
                    await CopyFileToContainer(containerId!, tempFile, $"/workspace/{filePath}");
                    System.IO.File.Delete(tempFile);
                    
                    await SendSlackMessage(slack, $"📄 Created file in container: {filePath}");
                    await LogToDb(task.Id, $"Created file: {filePath}");
                    await LogThought(task.Id, iteration, ThoughtType.Action, $"Created file: {filePath}", "create_file", filePath);
                }
                else if (action == "research")
                {
                    var details = actionDoc.RootElement.GetProperty("details").GetString() ?? "";
                    await SendSlackMessage(slack, $"🔍 Researching: {details}");
                    await LogToDb(task.Id, $"Research: {details}");
                    await LogThought(task.Id, iteration, ThoughtType.Planning, $"Research: {details}");
                    
                    var researchResult = await DoOnlineResearch(details);
                    await LogToDb(task.Id, $"Research results: {researchResult}");
                    await LogThought(task.Id, iteration, ThoughtType.Observation, researchResult);
                    await SendSlackMessage(slack, $"📚 Research summary: {researchResult.Substring(0, Math.Min(MaxSlackMessagePreviewLength, researchResult.Length))}");
                }
            }
            catch (Exception ex)
            {
                await LogToDb(task.Id, $"Error parsing AI response: {ex.Message}");
                await LogThought(task.Id, iteration, ThoughtType.Error, $"Error: {ex.Message}");
                await SendSlackMessage(slack, $"⚠️ Error processing AI response: {ex.Message}");
            }

            await Task.Delay(2000); // Rate limiting
        }

        // If task involves code/files and is completed, copy from container and create branch/PR
        if (completed)
        {
            // Copy files from container to local temp directory
            var localWorkingDir = Path.Combine("/tmp", $"luna-task-{task.Id}");
            Directory.CreateDirectory(localWorkingDir);
            await CopyFileFromContainer(containerId!, "/workspace/.", localWorkingDir);
            
            if (Directory.GetFiles(localWorkingDir, "*", SearchOption.AllDirectories).Length > 0)
            {
                await SendSlackMessage(slack, $"📦 Creating branch and PR for task #{task.Id}...");
                await LogThought(task.Id, iteration, ThoughtType.UserUpdate, "Creating branch and PR");
                var (branchName, repoPath) = await CreateGitBranch(task.Id, task.Description);
            
                if (!string.IsNullOrEmpty(branchName) && !string.IsNullOrEmpty(repoPath))
                {
                    // Copy files from local working directory to repo
                    foreach (var file in Directory.GetFiles(localWorkingDir, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(localWorkingDir, file);
                        var destPath = Path.Combine(repoPath, relativePath);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);
                        System.IO.File.Copy(file, destPath, true);
                    }

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

        var text = message.Text?.Trim() ?? "";
        Console.WriteLine($"Received message: {text}");

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
                statusMsg += $"  • #{qt.Id}: {qt.Description.Substring(0, Math.Min(MaxDescriptionPreviewLength, qt.Description.Length))}\n";
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

                    // Send log separately if too long
                    if (!string.IsNullOrEmpty(task.Log))
                    {
                        var logMsg = $"**Log for Task #{task.Id}:**\n```{task.Log.Substring(0, Math.Min(MaxLogPreviewLength, task.Log.Length))}```";
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
                if (task != null && task.Status == TaskStatus.Queued)
                {
                    await UpdateTaskStatus(taskId, TaskStatus.Paused);
                    await SendSlackMessage(slack, $"⏸️ Task #{taskId} paused");
                }
                else
                {
                    await SendSlackMessage(slack, $"Cannot pause task #{taskId}");
                }
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
                    // If there's a current task, pause it
                    if (currentTask != null && currentTask.Id != taskId)
                    {
                        await UpdateTaskStatus(currentTask.Id, TaskStatus.Paused);
                        await SendSlackMessage(slack, $"⏸️ Paused current task #{currentTask.Id}");
                        lock (queueLock)
                        {
                            currentTask = null;
                        }
                    }

                    // Start or resume the specified task
                    if (task.Status == TaskStatus.Paused)
                    {
                        await UpdateTaskStatus(taskId, TaskStatus.Queued);
                        lock (queueLock)
                        {
                            taskQueue.Enqueue(task);
                        }
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} resumed");
                    }
                    else if (task.Status == TaskStatus.Queued)
                    {
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} is already queued");
                    }
                    else if (task.Status == TaskStatus.Running)
                    {
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} is already running");
                    }
                    else
                    {
                        await UpdateTaskStatus(taskId, TaskStatus.Queued);
                        lock (queueLock)
                        {
                            taskQueue.Enqueue(task);
                        }
                        await SendSlackMessage(slack, $"▶️ Task #{taskId} started");
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
                msg += $"#{qt.Id} [{qt.Status}]: {qt.Description.Substring(0, Math.Min(MaxDescriptionPreviewLength, qt.Description.Length))}\n";
            }

            await SendSlackMessage(slack, msg.Length > 0 ? msg : "Queue is empty");
        }
        else if (text.StartsWith("!help"))
        {
            var helpMsg = @"🤖 **LUNA Agent Commands**

**/status** - Show current task and queue
**/details <task_id>** - Get details about a specific task
**/queue** - Show all queued and paused tasks
**/pause <task_id>** - Pause a queued task
**/start <task_id>** - Start a task (pauses current task if needed, starts or resumes specified task)
**/stop <task_id>** - Stop a task
**/system** - Get current system status (CPU, RAM, temperature, Ollama)
/**/help** - Show this help message

**To create a new task**, simply send a message describing what you want me to do!";

            await SendSlackMessage(slack, helpMsg);
        }
        else if (text.StartsWith("/system"))
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
        else if (!string.IsNullOrEmpty(text) && !text.StartsWith("/"))
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

await SendSlackMessage(client, "🚀 LUNA Agent is online and ready!");

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
                                // Skip system messages
                                if (string.IsNullOrWhiteSpace(m.Text) || 
                                    m.Text.Contains("joined the channel") ||
                                    m.Text.Contains("LUNA Agent is online") ||
                                    m.Text.Contains("has joined") ||
                                    m.User == null)
                                    return false;
                                
                                // Parse Slack timestamp (format: "1234567890.123456")
                                if (double.TryParse(m.Timestamp.ToString("F6"), out var msgTimestamp))
                                    return msgTimestamp > lastMessageTimestamp && 
                                           !processedMessageTimestamps.Contains(m.Timestamp.ToString());
                                return false;
                            })
                            .OrderBy(m => m.Timestamp)
                            .ToList();

                        foreach (var message in newMessages)
                        {
                            try
                            {
                                var messageTimestampStr = message.Timestamp.ToString("F6");
                                if (double.TryParse(messageTimestampStr, out var ts))
                                    lastMessageTimestamp = ts;
                                
                                processedMessageTimestamps.Add(messageTimestampStr);

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
                Console.WriteLine($"⚠️  Error polling messages: {ex.Message}");
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
