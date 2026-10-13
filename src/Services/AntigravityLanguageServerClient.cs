namespace Loupedeck.AntigravityPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;
    using Loupedeck.AntigravityPlugin.Models;

    // One running Antigravity language server (the app starts one, the IDE one or more)
    public sealed class LanguageServerConnection
    {
        public Int32 Pid { get; init; }
        public Int32 Port { get; set; }
        public String Csrf { get; init; } = "";
        public AntigravityHost Host { get; init; }

        // IDE: the server of an editor window's agent panel (started with LSP); gone when all IDE windows are closed
        public Boolean ServesEditorWindow { get; init; }
    }

    // Talks to Antigravity's local language servers (Connect-RPC over HTTPS with a CSRF token),
    // the same API the Antigravity UI uses. Antigravity and Antigravity IDE are served side by side.
    public class AntigravityLanguageServerClient
    {
        private static readonly Lazy<AntigravityLanguageServerClient> _instance = new(() => new AntigravityLanguageServerClient());
        public static AntigravityLanguageServerClient Instance => _instance.Value;

        private const String ServicePath = "exa.language_server_pb.LanguageServerService";
        private const String StepWaiting = "CORTEX_STEP_STATUS_WAITING";
        private const String ResumeMessage = "Continue";
        private const Int32 RecentStepCount = 10;
        private static readonly TimeSpan QuotaCacheTime = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RunningDetailsRefresh = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly Object _lock = new();
        private readonly Dictionary<String, CachedDetails> _detailsCache = new(StringComparer.OrdinalIgnoreCase);

        private List<LanguageServerConnection> _servers = new();
        private Dictionary<String, LiveTrajectoryInfo> _lastTrajectories = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastDiscovery = DateTime.MinValue;
        private Boolean _appInForeground = true;
        private QuotaSummary _lastQuota;
        private DateTime _lastQuotaFetch = DateTime.MinValue;

        private sealed class CachedDetails
        {
            public String Key;
            public Int32 StepCount;
            public DateTime FetchedAt;
            public LiveTrajectoryDetails Details;
        }

        public AntigravityLanguageServerClient()
        {
            var handler = new HttpClientHandler
            {
                // The language server uses a self-signed certificate on 127.0.0.1
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => m.RequestUri?.IsLoopback == true
            };
            this._httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        }

        // Products whose language server is currently running
        public Boolean IsRunning(AntigravityHost host) => this.GetServers().Any(s => s.Host == host);

        // Products with an open window. With "Run in background" the app keeps its language server (and
        // running agents) alive after its window is closed; it then hides its Dock icon.
        public Boolean HasOpenWindow(AntigravityHost host)
        {
            var servers = this.GetServers();
            return host == AntigravityHost.Ide
                ? servers.Any(s => s.Host == AntigravityHost.Ide && s.ServesEditorWindow)
                : servers.Any(s => s.Host == AntigravityHost.App) && this._appInForeground;
        }

        // ---------------------------------------------------------------- Quota

        // Last known quota (the same account is used by both products); refreshed at most every 10 s unless forced.
        // Null until Antigravity answered once.
        public QuotaSummary GetLiveQuota(Boolean forceRefresh = false)
        {
            lock (this._lock)
            {
                if (!forceRefresh && DateTime.UtcNow - this._lastQuotaFetch < QuotaCacheTime)
                {
                    return this._lastQuota;
                }
                this._lastQuotaFetch = DateTime.UtcNow;
            }

            QuotaSummary quota = null;
            foreach (var server in this.GetServers())
            {
                using var doc = this.Call(server, "RetrieveUserQuotaSummary", forceRefresh ? new { forceRefresh = true } : new { });
                quota = doc == null ? null : ParseQuota(doc.RootElement);
                if (quota != null)
                {
                    break;
                }
            }

            lock (this._lock)
            {
                if (quota != null)
                {
                    this._lastQuota = quota;
                }
                return this._lastQuota;
            }
        }

        private static QuotaSummary ParseQuota(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var quota = new QuotaSummary { IsLive = true };
            var thirdPartySeen = false;

            foreach (var group in groups.EnumerateArray())
            {
                var name = GetString(group, "displayName");

                // "Gemini Models" vs. "Claude and GPT models"
                QuotaGroupSummary target;
                if (name.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    target = quota.Gemini;
                }
                else if (!thirdPartySeen)
                {
                    target = quota.ThirdParty;
                    thirdPartySeen = true;
                }
                else
                {
                    continue;
                }

                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var bucket in buckets.EnumerateArray())
                {
                    var id = GetString(bucket, "bucketId");
                    var window = GetString(bucket, "window");
                    var remaining = bucket.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetDouble() : 1.0;

                    var slot = window.Equals("5h", StringComparison.OrdinalIgnoreCase) || id.EndsWith("5h", StringComparison.OrdinalIgnoreCase)
                        ? target.FiveHour
                        : window.Equals("weekly", StringComparison.OrdinalIgnoreCase) || id.EndsWith("weekly", StringComparison.OrdinalIgnoreCase)
                            ? target.Weekly
                            : null;
                    if (slot == null)
                    {
                        continue;
                    }

                    slot.UsedPercent = Math.Clamp(Math.Round((1.0 - remaining) * 100.0, 1), 0.0, 100.0);
                    if (DateTime.TryParse(GetString(bucket, "resetTime"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var reset))
                    {
                        slot.ResetTimeUtc = reset;
                    }
                }
            }

            return quota;
        }

        // ---------------------------------------------------------------- Sessions

        // Loaded conversations of all running language servers. Null when no server answered.
        public Dictionary<String, LiveTrajectoryInfo> GetLiveTrajectories()
        {
            Dictionary<String, LiveTrajectoryInfo> dict = null;

            foreach (var server in this.GetServers())
            {
                using var doc = this.Call(server, "GetAllCascadeTrajectories", new { });
                if (doc == null)
                {
                    continue;
                }

                dict ??= new Dictionary<String, LiveTrajectoryInfo>(StringComparer.OrdinalIgnoreCase);
                if (!doc.RootElement.TryGetProperty("trajectorySummaries", out var summaries) || summaries.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in summaries.EnumerateObject())
                {
                    var val = prop.Value;
                    var workspace = "";
                    if (val.TryGetProperty("workspaces", out var wsArr) && wsArr.ValueKind == JsonValueKind.Array)
                    {
                        workspace = wsArr.EnumerateArray()
                            .Select(w => GetString(w, "workspaceFolderAbsoluteUri"))
                            .FirstOrDefault(u => !String.IsNullOrEmpty(u)) ?? "";
                    }

                    dict[prop.Name] = new LiveTrajectoryInfo
                    {
                        CascadeId = prop.Name,
                        Summary = GetString(val, "summary"),
                        Status = GetString(val, "status"),
                        TerminationReason = GetString(val, "latestExecutorTerminationReason"),
                        WorkspacePath = workspace,
                        StepCount = (Int32)ReadInt64(val, "stepCount", 0),
                        LastUserInputStepIndex = (Int32)ReadInt64(val, "lastUserInputStepIndex", -1),
                        LastModified = GetString(val, "lastModifiedTime"),
                        Server = server
                    };
                }
            }

            if (dict != null)
            {
                lock (this._lock)
                {
                    this._lastTrajectories = dict;
                }
            }
            return dict;
        }

        // Waiting state, token and context usage of a cascade. Only the last steps and the newest
        // generator metadata are fetched (the full trajectory can be tens of MB), and results are
        // reused until the cascade changes.
        public LiveTrajectoryDetails GetLiveTrajectoryDetails(LiveTrajectoryInfo trajectory)
        {
            var key = $"{trajectory.StepCount}|{trajectory.LastModified}|{trajectory.Status}";

            CachedDetails cached;
            lock (this._lock)
            {
                this._detailsCache.TryGetValue(trajectory.CascadeId, out cached);
            }

            if (cached != null && cached.Key == key &&
                (!trajectory.IsRunning || DateTime.UtcNow - cached.FetchedAt < RunningDetailsRefresh))
            {
                return cached.Details;
            }

            var steps = this.GetRecentSteps(trajectory.Server, trajectory.CascadeId, trajectory.StepCount);
            if (steps == null)
            {
                return cached?.Details;
            }

            var lastResponse = steps.LastOrDefault(s => s.TryGetProperty("plannerResponse", out _));
            var stopReason = lastResponse.ValueKind == JsonValueKind.Object ? GetString(lastResponse.GetProperty("plannerResponse"), "stopReason") : "";
            var details = new LiveTrajectoryDetails
            {
                IsWaitingForUser = steps.Any(s => GetString(s, "status") == StepWaiting),
                LastResponseCanceled = stopReason.Contains("CLIENT_STREAM_ERROR", StringComparison.OrdinalIgnoreCase) ||
                                       stopReason.Contains("CANCEL", StringComparison.OrdinalIgnoreCase)
            };

            // Usage only changes when new steps arrive
            if (cached != null && cached.StepCount == trajectory.StepCount)
            {
                details.InputTokens = cached.Details.InputTokens;
                details.OutputTokens = cached.Details.OutputTokens;
                details.CacheReadTokens = cached.Details.CacheReadTokens;
                details.ContextUsedTokens = cached.Details.ContextUsedTokens;
                details.ContextMaxTokens = cached.Details.ContextMaxTokens;
                details.ModelGroup = cached.Details.ModelGroup;
            }
            else
            {
                this.ReadLatestUsage(trajectory, steps, details);

                // While a response is still being generated its metadata has no usage yet: keep the last known values
                if (cached != null && details.ContextMaxTokens == 0)
                {
                    details.ContextUsedTokens = cached.Details.ContextUsedTokens;
                    details.ContextMaxTokens = cached.Details.ContextMaxTokens;
                }
                if (cached != null && details.InputTokens == 0 && details.CacheReadTokens == 0)
                {
                    details.InputTokens = cached.Details.InputTokens;
                    details.OutputTokens = cached.Details.OutputTokens;
                    details.CacheReadTokens = cached.Details.CacheReadTokens;
                }
                details.ModelGroup ??= cached?.Details.ModelGroup;
            }

            lock (this._lock)
            {
                this._detailsCache[trajectory.CascadeId] = new CachedDetails
                {
                    Key = key,
                    StepCount = trajectory.StepCount,
                    FetchedAt = DateTime.UtcNow,
                    Details = details
                };
            }

            return details;
        }

        private List<JsonElement> GetRecentSteps(LanguageServerConnection server, String cascadeId, Int32 stepCount) =>
            this.GetSteps(server, cascadeId, Math.Max(0, stepCount - RecentStepCount));

        private List<JsonElement> GetSteps(LanguageServerConnection server, String cascadeId, Int32 stepOffset)
        {
            using var doc = this.Call(server, "GetCascadeTrajectorySteps", new { cascadeId, stepOffset });
            if (doc == null || !doc.RootElement.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return steps.EnumerateArray().Select(s => s.Clone()).ToList();
        }

        private void ReadLatestUsage(LiveTrajectoryInfo trajectory, List<JsonElement> steps, LiveTrajectoryDetails details)
        {
            // Each step points at its generator metadata entry; the newest one holds the current usage
            var metadataIndex = steps
                .Select(s => s.TryGetProperty("metadata", out var m) && m.TryGetProperty("sourceTrajectoryStepInfo", out var info)
                    ? ReadInt64(info, "metadataIndex", -1)
                    : -1)
                .DefaultIfEmpty(-1)
                .Max();
            if (metadataIndex < 0)
            {
                return;
            }

            // The newest entries (~3 KB each); the very newest may still be generating and lack usage
            using var doc = this.Call(trajectory.Server, "GetCascadeTrajectoryGeneratorMetadata",
                new { cascadeId = trajectory.CascadeId, generatorMetadataOffset = Math.Max(0, metadataIndex - 2) });
            if (doc == null || !doc.RootElement.TryGetProperty("generatorMetadata", out var metas) || metas.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var chatModels = metas.EnumerateArray()
                .Select(m => m.TryGetProperty("chatModel", out var cm) ? cm : default)
                .Where(cm => cm.ValueKind == JsonValueKind.Object)
                .ToList();

            var usage = chatModels
                .Select(cm => cm.TryGetProperty("usage", out var u) ? u : default)
                .LastOrDefault(u => u.ValueKind == JsonValueKind.Object);
            if (usage.ValueKind == JsonValueKind.Object)
            {
                details.InputTokens = ReadInt64(usage, "inputTokens", 0);
                details.OutputTokens = ReadInt64(usage, "outputTokens", 0);
                details.CacheReadTokens = ReadInt64(usage, "cacheReadTokens", 0);

                // API_PROVIDER_GOOGLE_GEMINI / _GOOGLE_VERTEX vs. _ANTHROPIC_VERTEX / _OPENAI_VERTEX
                var provider = GetString(usage, "apiProvider");
                if (provider.Length > 0)
                {
                    details.ModelGroup = provider.Contains("ANTHROPIC", StringComparison.OrdinalIgnoreCase) ||
                                         provider.Contains("OPENAI", StringComparison.OrdinalIgnoreCase)
                        ? QuotaGroup.ThirdParty
                        : QuotaGroup.Gemini;
                }
            }

            // Exact context fill and the context window of the model in use
            var context = chatModels
                .Select(cm => cm.TryGetProperty("chatStartMetadata", out var start) && start.TryGetProperty("contextWindowMetadata", out var c) ? c : default)
                .LastOrDefault(c => c.ValueKind == JsonValueKind.Object);
            if (context.ValueKind == JsonValueKind.Object)
            {
                details.ContextUsedTokens = ReadInt64(context, "estimatedTokensUsed", 0);
                details.ContextMaxTokens = ReadInt64(context, "maxContextTokens", 0);
            }
        }

        // ---------------------------------------------------------------- Actions

        // Answers the pending permission request like the buttons in Antigravity.
        // cascadeId == null answers every cascade that is waiting.
        public Boolean SendDecision(String cascadeId, PermissionDecision decision)
        {
            var trajectories = this.GetLiveTrajectories();
            if (trajectories == null)
            {
                PluginLog.Warning($"[{decision}] Language server not available.");
                return false;
            }

            var targets = cascadeId == null
                ? trajectories.Values.ToList()
                : trajectories.TryGetValue(cascadeId, out var t) ? new List<LiveTrajectoryInfo> { t } : new List<LiveTrajectoryInfo>();

            var success = false;
            foreach (var trajectory in targets)
            {
                var interaction = this.BuildInteraction(trajectory, decision);
                if (interaction == null)
                {
                    PluginLog.Info($"[{decision}] No pending interaction for {trajectory.CascadeId}");
                    continue;
                }

                using var doc = this.Call(trajectory.Server, "HandleCascadeUserInteraction", new JsonObject
                {
                    ["cascadeId"] = trajectory.CascadeId,
                    ["interaction"] = interaction
                });

                if (doc != null)
                {
                    PluginLog.Info($"[{decision}] Answered {trajectory.CascadeId}: {interaction.ToJsonString()}");
                    success = true;
                }
            }

            return success;
        }

        // Stops the running agent of a cascade (same as the stop button in Antigravity)
        public Boolean CancelCascade(String cascadeId)
        {
            var server = this.ServerFor(cascadeId);
            using var doc = server == null ? null : this.Call(server, "CancelCascadeInvocation", new { cascadeId });
            PluginLog.Info($"[Stop] CancelCascadeInvocation for {cascadeId}: {(doc != null ? "ok" : "failed")}");
            return doc != null;
        }

        // Continues a stopped cascade: sends "Continue" with the exact settings (model, mode, tools)
        // of the user's last message, as if it had been typed into the chat.
        public Boolean ResumeCascade(String cascadeId)
        {
            var trajectory = this.TrajectoryFor(cascadeId);
            if (trajectory == null || trajectory.LastUserInputStepIndex < 0)
            {
                PluginLog.Warning($"[Resume] No user message found for {cascadeId}");
                return false;
            }

            var userInput = this.GetSteps(trajectory.Server, cascadeId, trajectory.LastUserInputStepIndex)?
                .Select(s => s.TryGetProperty("userInput", out var ui) ? ui : default)
                .FirstOrDefault(ui => ui.ValueKind == JsonValueKind.Object && ui.TryGetProperty("userConfig", out _));
            if (userInput == null || userInput.Value.ValueKind != JsonValueKind.Object)
            {
                PluginLog.Warning($"[Resume] Settings of the last message not found for {cascadeId}");
                return false;
            }

            var body = new JsonObject
            {
                ["cascadeId"] = cascadeId,
                ["items"] = new JsonArray(new JsonObject { ["text"] = ResumeMessage }),
                ["cascadeConfig"] = JsonNode.Parse(userInput.Value.GetProperty("userConfig").GetRawText())
            };
            using var doc = this.Call(trajectory.Server, "SendUserCascadeMessage", body);
            PluginLog.Info($"[Resume] SendUserCascadeMessage for {cascadeId}: {(doc != null ? "ok" : "failed")}");
            return doc != null;
        }

        private JsonObject BuildInteraction(LiveTrajectoryInfo trajectory, PermissionDecision decision)
        {
            var steps = this.GetRecentSteps(trajectory.Server, trajectory.CascadeId, trajectory.StepCount);
            var step = steps?.LastOrDefault(s => GetString(s, "status") == StepWaiting);
            if (step == null || step.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var waiting = step.Value;
            if (!waiting.TryGetProperty("metadata", out var meta) || !meta.TryGetProperty("sourceTrajectoryStepInfo", out var info))
            {
                return null;
            }

            var allow = decision != PermissionDecision.Deny;
            var scope = decision == PermissionDecision.AllowConversation ? "PERMISSION_SCOPE_CONVERSATION" : "PERMISSION_SCOPE_ONCE";
            var interaction = new JsonObject
            {
                ["trajectoryId"] = GetString(info, "trajectoryId"),
                ["stepIndex"] = ReadInt64(info, "stepIndex", 0)
            };

            // File access request (e.g. reading a file outside the workspace)
            var filePath = waiting.EnumerateObject()
                .Select(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("filePermissionRequest", out var fpr) ? GetString(fpr, "absolutePathUri") : "")
                .FirstOrDefault(p => !String.IsNullOrEmpty(p));
            if (!String.IsNullOrEmpty(filePath))
            {
                interaction["filePermission"] = new JsonObject { ["allow"] = allow, ["scope"] = scope, ["absolutePathUri"] = filePath };
                return interaction;
            }

            // Otherwise answer the requested interaction type (oneof name == JSON key)
            var requested = waiting.TryGetProperty("requestedInteraction", out var ri) && ri.ValueKind == JsonValueKind.Object
                ? ri.EnumerateObject().Select(p => p.Name).FirstOrDefault()
                : null;

            switch (requested)
            {
                case null:
                    return null;
                case "permission":
                    interaction["permission"] = new JsonObject { ["allow"] = allow, ["scope"] = scope };
                    break;
                case "runCommand":
                    // Commands have no scope; "allow in conversation" approves this command once
                    var commandLine = waiting.TryGetProperty("runCommand", out var rc) ? GetString(rc, "commandLine") : "";
                    interaction["runCommand"] = new JsonObject { ["confirm"] = allow, ["proposedCommandLine"] = commandLine, ["submittedCommandLine"] = commandLine };
                    break;
                default:
                    // openBrowserUrl, captureBrowserScreenshot, executeBrowserJavascript, mcp, readUrlContent, ...
                    interaction[requested] = new JsonObject { ["confirm"] = allow };
                    break;
            }

            return interaction;
        }

        private LiveTrajectoryInfo TrajectoryFor(String cascadeId)
        {
            if (String.IsNullOrEmpty(cascadeId))
            {
                return null;
            }
            var trajectories = this.GetLiveTrajectories();
            return trajectories != null && trajectories.TryGetValue(cascadeId, out var t) ? t : null;
        }

        private LanguageServerConnection ServerFor(String cascadeId)
        {
            lock (this._lock)
            {
                if (cascadeId != null && this._lastTrajectories.TryGetValue(cascadeId, out var known))
                {
                    return known.Server;
                }
            }
            return this.TrajectoryFor(cascadeId)?.Server;
        }

        // ---------------------------------------------------------------- Transport

        private JsonDocument Call(LanguageServerConnection server, String method, Object body)
        {
            try
            {
                return this.Send(server.Port, server.Csrf, method, body);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // Antigravity restarted or closed: forget this server, it is rediscovered next time
                PluginLog.Warning($"[LanguageServer] {method} on {server.Host} failed: {ex.Message}");
                lock (this._lock)
                {
                    this._servers = this._servers.Where(s => s != server).ToList();
                    this._lastDiscovery = DateTime.MinValue;
                }
                return null;
            }
            catch (JsonException ex)
            {
                PluginLog.Warning($"[LanguageServer] {method} returned invalid JSON: {ex.Message}");
                return null;
            }
        }

        private JsonDocument Send(Int32 port, String csrf, String method, Object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/{ServicePath}/{method}");
            req.Headers.Add("x-codeium-csrf-token", csrf);
            var json = body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = this._httpClient.Send(req);
            using var stream = resp.Content.ReadAsStream();
            if (!resp.IsSuccessStatusCode)
            {
                using var reader = new System.IO.StreamReader(stream);
                PluginLog.Warning($"[LanguageServer] {method} returned {(Int32)resp.StatusCode}: {reader.ReadToEnd()}");
                return null;
            }

            return JsonDocument.Parse(stream);
        }

        // Running language servers; the process list is re-checked every 3 s, ports are only probed for new processes
        private List<LanguageServerConnection> GetServers()
        {
            lock (this._lock)
            {
                if (DateTime.UtcNow - this._lastDiscovery < DiscoveryInterval)
                {
                    return this._servers;
                }
                this._lastDiscovery = DateTime.UtcNow;

                var servers = new List<LanguageServerConnection>();
                try
                {
                    foreach (var line in RunTool("ps", "-ax -o pid,command").Split('\n'))
                    {
                        var server = this.ParseServer(line);
                        if (server != null)
                        {
                            servers.Add(server);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"[LanguageServer] Discovery failed: {ex.Message}");
                    return this._servers;
                }

                // The app first: its quota and conversations are the reference
                this._servers = servers.OrderBy(s => s.Host).ThenBy(s => s.Pid).ToList();

                // "Foreground" while it shows a window (and its Dock icon), "UIElement" when running in the background
                if (this._servers.Any(s => s.Host == AntigravityHost.App))
                {
                    var type = RunTool("lsappinfo", "info -only ApplicationType com.google.antigravity");
                    this._appInForeground = !type.Contains("UIElement", StringComparison.OrdinalIgnoreCase);
                }
                return this._servers;
            }
        }

        // Reuses a known server or probes the ports of a new language_server process
        private LanguageServerConnection ParseServer(String line)
        {
            if (!line.Contains("language_server") || !line.Contains("--csrf_token"))
            {
                return null;
            }

            var csrf = Regex.Match(line, @"--csrf_token\s+([a-zA-Z0-9\-]+)").Groups[1].Value;
            var pidText = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (String.IsNullOrEmpty(csrf) || !Int32.TryParse(pidText, out var pid))
            {
                return null;
            }

            var known = this._servers.FirstOrDefault(s => s.Pid == pid && s.Csrf == csrf);
            if (known != null)
            {
                return known;
            }

            var host = line.Contains("Antigravity IDE.app", StringComparison.OrdinalIgnoreCase) || line.Contains("--app_data_dir antigravity-ide")
                ? AntigravityHost.Ide
                : AntigravityHost.App;

            var listening = RunTool("lsof", $"-a -p {pid} -iTCP -sTCP:LISTEN -P -n");
            foreach (Match m in Regex.Matches(listening, @":(\d+)\s+\(LISTEN\)"))
            {
                var port = Int32.Parse(m.Groups[1].Value);
                try
                {
                    using var probe = this.Send(port, csrf, "GetAllCascadeTrajectories", new { });
                    if (probe != null)
                    {
                        PluginLog.Info($"[LanguageServer] Connected to {host} (pid {pid}) on port {port}");
                        return new LanguageServerConnection
                        {
                            Pid = pid,
                            Port = port,
                            Csrf = csrf,
                            Host = host,
                            ServesEditorWindow = line.Contains("--enable_lsp")
                        };
                    }
                }
                catch
                {
                    // Not the Connect-RPC port, try the next one
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- Helpers

        private static String RunTool(String fileName, String arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return "";
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);
            return output;
        }

        private static String GetString(JsonElement el, String property) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? ""
                : "";

        // int64 values arrive as JSON strings in proto3 JSON
        private static Int64 ReadInt64(JsonElement el, String property, Int64 fallback)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(property, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && Int64.TryParse(v.GetString(), out var s)) return s;
            return fallback;
        }
    }
}
