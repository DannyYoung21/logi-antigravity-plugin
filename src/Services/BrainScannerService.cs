namespace Loupedeck.AntigravityPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using Loupedeck.AntigravityPlugin.Models;

    // Discovers Antigravity sessions (Antigravity app: ~/.gemini/antigravity, Antigravity IDE:
    // ~/.gemini/antigravity-ide and its language servers), merges in live status from the
    // language servers, assigns them to keypad slots and keeps the quota up to date.
    public class BrainScannerService : IDisposable
    {
        private static readonly Lazy<BrainScannerService> _instance = new(() => new BrainScannerService());
        public static BrainScannerService Instance => _instance.Value;

        private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(48);
        private static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(7);
        private static readonly TimeSpan ProcessCheckInterval = TimeSpan.FromSeconds(2);

        private readonly DataSource[] _sources;
        private readonly Timer _pollTimer;
        private readonly SlotAssignmentService _slotAssignment = new();
        private readonly Dictionary<String, (AgentSessionState State, DateTime Since)> _statusHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, TranscriptInfo> _transcriptCache = new(StringComparer.Ordinal);
        private readonly Object _lock = new();
        private readonly Object _scanLock = new();

        private Dictionary<Int32, AntigravitySession> _sessionsBySlot = new();
        private QuotaSummary _quota = new();
        private String _lastChangeSignature = "";
        private String _heldKey;
        private Int32 _isPolling;

        private List<String> _lastGlobalStop = new();
        private Int32 _nextWaitingIndex;

        public Int32 CurrentActiveSlot { get; set; } = 1;
        public Boolean IsSyncing { get; private set; }
        public SlotOrderStrategy SlotOrder { get; private set; } = SlotOrderStrategy.Sticky;

        public event Action OnSessionsChanged;

        // Data folder of one Antigravity product
        private sealed class DataSource
        {
            public AntigravityHost Host;
            public String BrainDir;
            public String ConvDir;
            public String SummariesDbPath;
            public Dictionary<String, (String Title, String Workspace, Boolean NotFullyIdle)> DbSummaries = new();
            public DateTime DbSummariesStamp = DateTime.MinValue;
        }

        // What a transcript says, cached until the file changes
        private sealed class TranscriptInfo
        {
            public Int64 Length;
            public DateTime WriteTime;
            public String ProjectName = "";
            public String FirstPrompt = "";
            public String LatestStatus = "";
            public Boolean AsksQuestion;
            public Int64 InputTokens;
            public Int64 OutputTokens;
            public Int64 CacheReadTokens;
        }

        public BrainScannerService()
        {
            var geminiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
            this._sources = new[] { (AntigravityHost.App, "antigravity"), (AntigravityHost.Ide, "antigravity-ide") }
                .Select(x => new DataSource
                {
                    Host = x.Item1,
                    BrainDir = Path.Combine(geminiDir, x.Item2, "brain"),
                    ConvDir = Path.Combine(geminiDir, x.Item2, "conversations"),
                    SummariesDbPath = Path.Combine(geminiDir, x.Item2, "conversation_summaries.db")
                })
                .ToArray();

            this._pollTimer = new Timer(this.OnPollTick, null, 100, 400); // 400ms polling for responsive updates
        }

        public IReadOnlyList<AntigravitySession> Sessions
        {
            get
            {
                lock (this._lock)
                {
                    return this._sessionsBySlot.Values.ToList();
                }
            }
        }

        public QuotaSummary Quota
        {
            get
            {
                lock (this._lock)
                {
                    return this._quota;
                }
            }
        }

        public AntigravitySession GetSessionForSlot(Int32 slotNumber)
        {
            lock (this._lock)
            {
                return this._sessionsBySlot.TryGetValue(slotNumber, out var session) ? session : null;
            }
        }

        public AntigravitySession GetActiveSession() => this.GetSessionForSlot(this.CurrentActiveSlot) ?? this.GetSessionForSlot(1);

        // ---------------------------------------------------------------- Slot order

        public void SetSlotOrder(SlotOrderStrategy strategy)
        {
            lock (this._scanLock)
            {
                if (this.SlotOrder == strategy) return;
                this.SlotOrder = strategy;
                this._slotAssignment.RequestResort();
            }
            PluginLog.Info($"[SLOTS] Slot order set to {strategy}");
            this.TriggerManualSync();
        }

        // While a slot folder is open its session stays in that slot (same as Claude plugin)
        public void HoldSlot(Int32 slot)
        {
            lock (this._scanLock)
            {
                this._heldKey = this._slotAssignment.GetKey(slot);
            }
        }

        public void ReleaseSlot(Int32 slot)
        {
            lock (this._scanLock)
            {
                var heldSlot = this._slotAssignment.GetSlot(this._heldKey);
                if (heldSlot == null || heldSlot == slot)
                {
                    this._heldKey = null;
                }
            }
        }

        // ---------------------------------------------------------------- Actions

        public void TriggerManualSync()
        {
            this.IsSyncing = true;
            this.OnSessionsChanged?.Invoke();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AntigravityLanguageServerClient.Instance.GetLiveQuota(forceRefresh: true);
                    this.Scan(forceNotify: true);
                    Thread.Sleep(300); // keep "Sync..." visible for a moment
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"TriggerManualSync error: {ex.Message}");
                }
                finally
                {
                    this.IsSyncing = false;
                    this.OnSessionsChanged?.Invoke();
                }
            });
        }

        // Session that waits for an answer: the given one, else the last opened slot, else any
        public AntigravitySession GetPendingSession(AntigravitySession preferred = null)
        {
            if (preferred?.State == AgentSessionState.Permission) return preferred;
            var active = this.GetActiveSession();
            if (active?.State == AgentSessionState.Permission) return active;
            return this.GetWaitingSessions().FirstOrDefault();
        }

        // Waiting sessions, the one waiting longest first
        public List<AntigravitySession> GetWaitingSessions() =>
            this.Sessions.Where(s => s.State == AgentSessionState.Permission).OrderBy(s => s.StatusSince).ToList();

        // Answers the pending request. session == null answers every waiting session (Allow Once only).
        public void AnswerPermission(AntigravitySession session, PermissionDecision decision)
        {
            var cascadeId = session?.CascadeId;
            PluginLog.Info($"[{decision}] Requested for {session?.DisplayName ?? "all sessions"} (cascade: {cascadeId ?? "all"})");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!AntigravityLanguageServerClient.Instance.SendDecision(cascadeId, decision) && decision == PermissionDecision.AllowOnce)
                {
                    ClickAllowOnceButton();
                }
                this.TriggerManualSync();
            });
        }

        // Stop key of a slot: stops a running agent, resumes a stopped one
        public void StopOrResume(AntigravitySession session)
        {
            if (session == null)
            {
                return;
            }

            var resume = session.State == AgentSessionState.Stopped;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var client = AntigravityLanguageServerClient.Instance;
                if (resume)
                {
                    client.ResumeCascade(session.CascadeId);
                }
                else
                {
                    client.CancelCascade(session.CascadeId);
                }
                this.TriggerManualSync();
            });
        }

        // What the global Stop key does right now
        public Boolean GlobalKeyResumes =>
            !this.Sessions.Any(s => s.State == AgentSessionState.Thinking) &&
            this.Sessions.Any(s => s.State == AgentSessionState.Stopped);

        // Global Stop key: stops every running agent; when nothing runs, resumes what it stopped last
        // (or the most recently stopped session)
        public void StopOrResumeAll()
        {
            var sessions = this.Sessions;
            var resume = this.GlobalKeyResumes;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var client = AntigravityLanguageServerClient.Instance;
                if (resume)
                {
                    var stopped = sessions.Where(s => s.State == AgentSessionState.Stopped).ToList();
                    var targets = stopped.Where(s => this._lastGlobalStop.Contains(s.CascadeId)).ToList();
                    if (targets.Count == 0)
                    {
                        targets = stopped.OrderByDescending(s => s.StatusSince).Take(1).ToList();
                    }
                    foreach (var s in targets)
                    {
                        client.ResumeCascade(s.CascadeId);
                    }
                    this._lastGlobalStop = new List<String>();
                }
                else
                {
                    var running = (client.GetLiveTrajectories()?.Values ?? Enumerable.Empty<LiveTrajectoryInfo>())
                        .Where(t => t.IsRunning)
                        .Select(t => t.CascadeId)
                        .ToList();
                    foreach (var cascadeId in running)
                    {
                        client.CancelCascade(cascadeId);
                    }
                    this._lastGlobalStop = running;
                }
                this.TriggerManualSync();
            });
        }

        // Next session that waits for permission (cycles on repeated presses): shown in Antigravity
        // and made the "last opened slot" so Context/Tokens/Allow keys follow it
        public void ShowNextWaitingSession()
        {
            var waiting = this.GetWaitingSessions();
            if (waiting.Count == 0)
            {
                return;
            }

            var session = waiting[this._nextWaitingIndex++ % waiting.Count];
            var slot = this._slotAssignment.GetSlot(session.SlotKey);
            if (slot.HasValue)
            {
                this.CurrentActiveSlot = slot.Value;
            }
            this.OpenSession(session);
            this.OnSessionsChanged?.Invoke();
        }

        // Brings the session's Antigravity product to the front. Neither the app nor the IDE lets other
        // programs open a specific conversation, so the session is also made the "last opened slot".
        public void OpenSession(AntigravitySession session) =>
            ActivateApp(session?.Host ?? this.PreferredHost());

        // Brings the Antigravity product of the last opened slot to the front (app by default)
        public void FocusAntigravityWindow() => ActivateApp(this.PreferredHost());

        // New conversation in the product of the last opened slot, via its own shortcut (Cmd+Shift+L in
        // both the app and the IDE). Needs accessibility access for the Logi Plugin Service.
        public void StartNewConversation()
        {
            var host = this.PreferredHost();
            RunDetached("osascript",
                "-e", $"tell application \"{AppName(host)}\" to activate",
                "-e", "delay 0.4",
                "-e", "tell application \"System Events\" to keystroke \"l\" using {command down, shift down}");
            PluginLog.Info($"[NewConversation] Shortcut sent to {AppName(host)}");
        }

        private AntigravityHost PreferredHost()
        {
            var client = AntigravityLanguageServerClient.Instance;
            var active = this.GetActiveSession();
            if (active != null && client.IsRunning(active.Host)) return active.Host;
            if (client.IsRunning(AntigravityHost.App)) return AntigravityHost.App;
            return client.IsRunning(AntigravityHost.Ide) ? AntigravityHost.Ide : AntigravityHost.App;
        }

        private static String AppName(AntigravityHost host) => host == AntigravityHost.Ide ? "Antigravity IDE" : "Antigravity";

        private static void ActivateApp(AntigravityHost host) => RunDetached("open", "-a", AppName(host));

        // Fallback via UI automation: only ever clicks a one-time approval, never "Always allow"
        private static void ClickAllowOnceButton()
        {
            const String script = """
                tell application "System Events"
                    set appProc to missing value
                    if exists process "Antigravity" then
                        set appProc to process "Antigravity"
                    else if exists (first process whose bundle identifier is "com.google.antigravity-ide") then
                        set appProc to first process whose bundle identifier is "com.google.antigravity-ide"
                    end if
                    if appProc is not missing value then
                        tell appProc
                            set frontmost to true
                            repeat with w in windows
                                try
                                    set candidateBtns to (every button of entire contents of w whose (name contains "Allow once" or name contains "Allow Once" or name contains "Allow this time" or name contains "Einmal erlauben"))
                                    if (count of candidateBtns) > 0 then
                                        click item 1 of candidateBtns
                                        exit repeat
                                    end if
                                end try
                            end repeat
                        end tell
                    end if
                end tell
                """;
            PluginLog.Info("[AllowOnce] Nothing approved via language server, trying UI fallback");
            RunDetached("osascript", "-e", script);
        }

        private static void RunDetached(String fileName, params String[] arguments)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in arguments)
                {
                    psi.ArgumentList.Add(arg);
                }
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Starting {fileName} failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- Scan

        private void OnPollTick(Object state)
        {
            if (Interlocked.CompareExchange(ref this._isPolling, 1, 0) != 0)
            {
                return;
            }
            try
            {
                this.Scan();
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"BrainScanner poll error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref this._isPolling, 0);
            }
        }

        private void Scan(Boolean forceNotify = false)
        {
            lock (this._scanLock)
            {
                this.ScanLocked(forceNotify);
            }
        }

        private void ScanLocked(Boolean forceNotify)
        {
            var nowUtc = DateTime.UtcNow;
            var client = AntigravityLanguageServerClient.Instance;
            var live = client.GetLiveTrajectories() ?? new Dictionary<String, LiveTrajectoryInfo>();

            // Conversations on disk, plus loaded conversations that have no transcript (Antigravity IDE)
            var sessions = this.ReadSessions(nowUtc);
            var trajectories = AttachTrajectories(sessions, live);
            foreach (var t in live.Values.Where(t => !trajectories.Values.Contains(t)))
            {
                var session = BuildLiveSession(t);
                sessions.Add(session);
                trajectories[session] = t;
            }

            // One tile per project: keep the most recent session of each project
            var tracked = sessions
                .OrderByDescending(s => s.LastUpdated)
                .DistinctBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .Where(s => nowUtc - s.LastUpdated < SessionWindow || s.State != AgentSessionState.Idle || trajectories.ContainsKey(s))
                .Take(SlotAssignmentService.MaxSlots * 2) // only the most recent projects compete for the slots
                .ToList();

            // Final status must be known BEFORE slots are assigned (Recency/Settled sort on it)
            foreach (var s in tracked)
            {
                if (trajectories.TryGetValue(s, out var t))
                {
                    ApplyLiveStatus(s, t, client.GetLiveTrajectoryDetails(t));
                }

                // Product closed: tiles fall back to Idle but keep the project name. With its window closed
                // but still running in the background, finished sessions turn Idle too; agents that still
                // work or wait for permission keep showing it (they can still be answered from the keypad).
                if (!client.IsRunning(s.Host) ||
                    (!client.HasOpenWindow(s.Host) && s.State != AgentSessionState.Thinking && s.State != AgentSessionState.Permission))
                {
                    s.State = AgentSessionState.Idle;
                }
            }

            this.UpdateStatusSince(tracked, nowUtc);
            this._slotAssignment.Sync(tracked, this.SlotOrder, this._heldKey);

            var slotMap = new Dictionary<Int32, AntigravitySession>();
            foreach (var s in tracked)
            {
                var slot = this._slotAssignment.GetSlot(s.SlotKey);
                if (slot.HasValue)
                {
                    slotMap[slot.Value] = s;
                }
            }

            var quota = client.GetLiveQuota() ?? new QuotaSummary();
            var signature = BuildChangeSignature(slotMap, quota);

            lock (this._lock)
            {
                this._sessionsBySlot = slotMap;
                this._quota = quota;
            }

            if (signature != this._lastChangeSignature || forceNotify)
            {
                this._lastChangeSignature = signature;
                this.OnSessionsChanged?.Invoke();
            }
        }

        // Everything a tile shows; also covers the reset countdowns so they tick every minute
        private static String BuildChangeSignature(Dictionary<Int32, AntigravitySession> slots, QuotaSummary quota)
        {
            var parts = slots.OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}:{kvp.Value.Id}:{kvp.Value.DisplayName}:{kvp.Value.State}:{kvp.Value.ModelGroup}:" +
                               $"{kvp.Value.InputTokens}:{kvp.Value.OutputTokens}:{kvp.Value.CacheReadTokens}:{kvp.Value.ContextUsedTokens}");

            String Window(QuotaWindow w) => $"{w.UsedPercent:0.0}/{(Int32?)w.RemainingUntilReset?.TotalMinutes}";
            var quotaPart = $"{quota.IsLive}|{Window(quota.Gemini.FiveHour)}|{Window(quota.Gemini.Weekly)}|{Window(quota.ThirdParty.FiveHour)}|{Window(quota.ThirdParty.Weekly)}";

            return String.Join(";", parts) + "#" + quotaPart;
        }

        private List<AntigravitySession> ReadSessions(DateTime nowUtc)
        {
            var sessions = new List<AntigravitySession>();
            var seen = new HashSet<String>(StringComparer.Ordinal);

            foreach (var source in this._sources.Where(src => Directory.Exists(src.BrainDir)))
            {
                var dbSummaries = GetDbSummaries(source);
                var candidates = new DirectoryInfo(source.BrainDir).GetDirectories()
                    .Where(d => !d.Name.StartsWith(".") && d.Name != "tempmediaStorage")
                    .Select(d => (Id: d.Name, Transcript: Path.Combine(d.FullName, ".system_generated", "logs", "transcript.jsonl")))
                    .Where(c => File.Exists(c.Transcript))
                    .Select(c => (c.Id, c.Transcript, LastActive: GetLastActivity(source, c.Id, c.Transcript)))
                    .Where(c => nowUtc - c.LastActive < CandidateWindow)
                    .OrderByDescending(c => c.LastActive)
                    .Take(48);

                foreach (var c in candidates)
                {
                    seen.Add(c.Transcript);
                    var info = this.GetTranscriptInfo(c.Transcript);
                    if (info == null)
                    {
                        continue;
                    }

                    dbSummaries.TryGetValue(c.Id, out var meta);
                    var session = BuildSession(c.Id, c.LastActive, info, meta.Title, meta.Workspace, meta.NotFullyIdle, nowUtc);
                    session.Host = source.Host;
                    sessions.Add(session);
                }
            }

            // Forget transcripts that dropped out of the window
            foreach (var stale in this._transcriptCache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                this._transcriptCache.Remove(stale);
            }

            return sessions;
        }

        // Newest write of the transcript or the conversation database
        private static DateTime GetLastActivity(DataSource source, String id, String transcriptPath)
        {
            var lastActive = File.GetLastWriteTimeUtc(transcriptPath);
            foreach (var path in new[] { Path.Combine(source.ConvDir, $"{id}.db"), Path.Combine(source.ConvDir, $"{id}.db-wal") })
            {
                if (File.Exists(path))
                {
                    var t = File.GetLastWriteTimeUtc(path);
                    if (t > lastActive) lastActive = t;
                }
            }
            return lastActive;
        }

        private static AntigravitySession BuildSession(String id, DateTime lastActive, TranscriptInfo info,
            String dbTitle, String dbWorkspace, Boolean notFullyIdle, DateTime nowUtc)
        {
            // Project name: DB workspace, transcript workspace, DB title, first prompt, session id
            var projectName = Path.GetFileName(CleanWorkspacePath(dbWorkspace));
            if (String.IsNullOrWhiteSpace(projectName)) projectName = info.ProjectName;
            if (String.IsNullOrWhiteSpace(projectName)) projectName = dbTitle;
            if (String.IsNullOrWhiteSpace(projectName)) projectName = info.FirstPrompt;
            if (String.IsNullOrWhiteSpace(projectName) || projectName == "\\" || projectName == "/")
            {
                projectName = id.Length > 8 ? id.Substring(0, 8) : id;
            }

            var status = info.LatestStatus;
            var isDone = status.Equals("DONE", StringComparison.OrdinalIgnoreCase);
            var isError = status.Equals("ERROR", StringComparison.OrdinalIgnoreCase);
            var asksPermission = status.StartsWith("WAITING", StringComparison.OrdinalIgnoreCase) ||
                                 status.Equals("PENDING_APPROVAL", StringComparison.OrdinalIgnoreCase) ||
                                 (!isDone && !isError && info.AsksQuestion);
            var isRunning = status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                            ((nowUtc - lastActive).TotalSeconds < 15.0 && notFullyIdle);

            var state = asksPermission ? AgentSessionState.Permission
                : isRunning ? AgentSessionState.Thinking
                : isError ? AgentSessionState.Error
                : isDone && nowUtc - lastActive < TimeSpan.FromHours(24) ? AgentSessionState.Complete
                : AgentSessionState.Idle;

            return new AntigravitySession
            {
                Id = id,
                DisplayName = FormatShortTitle(projectName),
                WorkspaceUri = dbWorkspace ?? "",
                LastUpdated = lastActive,
                State = state,
                InputTokens = info.InputTokens,
                OutputTokens = info.OutputTokens,
                CacheReadTokens = info.CacheReadTokens
            };
        }

        // Session for a loaded conversation that has no transcript on disk (Antigravity IDE)
        private static AntigravitySession BuildLiveSession(LiveTrajectoryInfo t)
        {
            var name = Path.GetFileName(CleanWorkspacePath(t.WorkspacePath));
            if (String.IsNullOrWhiteSpace(name)) name = t.Summary;
            if (String.IsNullOrWhiteSpace(name)) name = "New Chat";

            return new AntigravitySession
            {
                Id = t.CascadeId,
                DisplayName = FormatShortTitle(name),
                Host = t.Host,
                WorkspaceUri = t.WorkspacePath,
                LastUpdated = DateTime.TryParse(t.LastModified, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified) ? modified : DateTime.UtcNow,
                State = AgentSessionState.Idle
            };
        }

        private static String FormatShortTitle(String raw)
        {
            raw = raw.Replace("file://", "").Trim();
            if (raw.StartsWith("Antigravity Plugin ", StringComparison.OrdinalIgnoreCase)) return raw.Substring(19);
            if (raw.StartsWith("Antigravity ", StringComparison.OrdinalIgnoreCase)) return raw.Substring(12);
            return raw.Length > 40 ? raw.Substring(0, 40) : raw; // the tile wraps and shortens it
        }

        // conversation_summaries.db via the sqlite3 CLI; only re-read when the database changed
        private static Dictionary<String, (String Title, String Workspace, Boolean NotFullyIdle)> GetDbSummaries(DataSource source)
        {
            if (!File.Exists(source.SummariesDbPath))
            {
                return source.DbSummaries;
            }

            var stamp = File.GetLastWriteTimeUtc(source.SummariesDbPath);
            var wal = source.SummariesDbPath + "-wal";
            if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > stamp)
            {
                stamp = File.GetLastWriteTimeUtc(wal);
            }
            if (stamp == source.DbSummariesStamp)
            {
                return source.DbSummaries;
            }

            var dict = new Dictionary<String, (String, String, Boolean)>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/sqlite3",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var arg in new[] { "-readonly", "-batch", "-list", "-separator", "|", source.SummariesDbPath,
                    "SELECT conversation_id, title, workspace_uris, not_fully_idle FROM conversation_summaries;" })
                {
                    psi.ArgumentList.Add(arg);
                }

                using var proc = Process.Start(psi);
                String line;
                while (proc != null && (line = proc.StandardOutput.ReadLine()) != null)
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        dict[parts[0].Trim()] = (parts[1].Trim(), parts[2].Trim(), parts.Length >= 4 && parts[3].Trim() == "1");
                    }
                }
                proc?.WaitForExit(1500);
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Error reading SQLite summaries: {ex.Message}");
            }

            // Also remembered when reading failed (e.g. an empty database), so it is not retried every scan
            source.DbSummaries = dict;
            source.DbSummariesStamp = stamp;
            return source.DbSummaries;
        }

        // Pairs sessions with their loaded conversation: by id, else (same product) by workspace folder or summary
        private static Dictionary<AntigravitySession, LiveTrajectoryInfo> AttachTrajectories(
            List<AntigravitySession> sessions, Dictionary<String, LiveTrajectoryInfo> live)
        {
            var result = new Dictionary<AntigravitySession, LiveTrajectoryInfo>();
            var used = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in sessions.Where(s => live.ContainsKey(s.Id)))
            {
                result[s] = live[s.Id];
                used.Add(s.Id);
            }

            // Most recent session wins the remaining conversations
            foreach (var s in sessions.Where(s => !result.ContainsKey(s)).OrderByDescending(s => s.LastUpdated))
            {
                var t = live.Values.FirstOrDefault(t => !used.Contains(t.CascadeId) && t.Host == s.Host && MatchesSession(t, s));
                if (t != null)
                {
                    result[s] = t;
                    used.Add(t.CascadeId);
                }
            }

            return result;
        }

        // Live status, tokens, context and model of a session's loaded conversation
        private static void ApplyLiveStatus(AntigravitySession s, LiveTrajectoryInfo trajectory, LiveTrajectoryDetails details)
        {
            s.LiveCascadeId = trajectory.CascadeId;
            if (String.IsNullOrEmpty(s.WorkspaceUri))
            {
                s.WorkspaceUri = trajectory.WorkspacePath;
            }
            if (DateTime.TryParse(trajectory.LastModified, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified) && modified > s.LastUpdated)
            {
                s.LastUpdated = modified;
            }

            if (details != null)
            {
                if (details.InputTokens > 0 || details.CacheReadTokens > 0)
                {
                    s.InputTokens = details.InputTokens;
                    s.OutputTokens = details.OutputTokens;
                    s.CacheReadTokens = details.CacheReadTokens;
                }
                s.ContextUsedTokens = details.ContextUsedTokens;
                s.ContextMaxTokens = details.ContextMaxTokens;
                s.ModelGroup = details.ModelGroup ?? s.ModelGroup;
            }

            var status = trajectory.Status;
            if (details?.IsWaitingForUser == true)
            {
                s.State = AgentSessionState.Permission;
            }
            else if (trajectory.IsRunning)
            {
                s.State = AgentSessionState.Thinking;
            }
            else if (status.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                s.State = AgentSessionState.Error;
            }
            else if (status.Contains("IDLE", StringComparison.OrdinalIgnoreCase))
            {
                s.State = trajectory.WasCanceled || details?.LastResponseCanceled == true ? AgentSessionState.Stopped
                    : trajectory.StepCount > 0 ? AgentSessionState.Complete
                    : AgentSessionState.Idle;
            }
        }

        // Fallback when the cascade id differs from the brain folder: match by workspace folder or summary
        private static Boolean MatchesSession(LiveTrajectoryInfo t, AntigravitySession s)
        {
            var wsName = Path.GetFileName(CleanWorkspacePath(t.WorkspacePath));
            if (!String.IsNullOrWhiteSpace(wsName) &&
                (s.DisplayName.Contains(wsName, StringComparison.OrdinalIgnoreCase) || wsName.Contains(s.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return !String.IsNullOrWhiteSpace(t.Summary) &&
                   (t.Summary.Contains(s.DisplayName, StringComparison.OrdinalIgnoreCase) || s.DisplayName.Contains(t.Summary, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateStatusSince(List<AntigravitySession> sessions, DateTime nowUtc)
        {
            foreach (var s in sessions)
            {
                if (this._statusHistory.TryGetValue(s.SlotKey, out var prev) && prev.State == s.State)
                {
                    s.StatusSince = prev.Since;
                    continue;
                }

                // First sighting: use last activity so the initial order is meaningful
                var since = this._statusHistory.ContainsKey(s.SlotKey) ? nowUtc : s.LastUpdated;
                this._statusHistory[s.SlotKey] = (s.State, since);
                s.StatusSince = since;
            }
        }

        private TranscriptInfo GetTranscriptInfo(String path)
        {
            try
            {
                var file = new FileInfo(path);
                if (this._transcriptCache.TryGetValue(path, out var cached) &&
                    cached.Length == file.Length && cached.WriteTime == file.LastWriteTimeUtc)
                {
                    return cached;
                }

                var info = ParseTranscript(path);
                if (info != null)
                {
                    info.Length = file.Length;
                    info.WriteTime = file.LastWriteTimeUtc;
                    this._transcriptCache[path] = info;
                }
                return info;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Error parsing transcript {path}: {ex.Message}");
                return null;
            }
        }

        private static TranscriptInfo ParseTranscript(String path)
        {
            List<String> lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                lines = new List<String>();
                String line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!String.IsNullOrWhiteSpace(line)) lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                return null;
            }

            var info = new TranscriptInfo
            {
                ProjectName = ExtractProjectName(lines),
                FirstPrompt = ExtractFirstUserPrompt(lines[0])
            };

            // Walk back from the end: the latest step decides the status, the latest planner response holds the tokens
            var haveLatest = false;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    var root = doc.RootElement;

                    if (!haveLatest)
                    {
                        haveLatest = true;
                        info.LatestStatus = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                        info.AsksQuestion = root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array &&
                            calls.EnumerateArray().Any(tc => tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n) &&
                                (n.GetString() ?? "") is var name && (name.Contains("question") || name.Contains("permission") || name.Contains("confirm")));
                    }

                    if (root.TryGetProperty("type", out var type) && type.GetString() == "PLANNER_RESPONSE")
                    {
                        if (root.TryGetProperty("input_tokens", out var it)) info.InputTokens = it.GetInt64();
                        if (root.TryGetProperty("output_tokens", out var ot)) info.OutputTokens = ot.GetInt64();
                        if (root.TryGetProperty("cache_read_tokens", out var ct)) info.CacheReadTokens = ct.GetInt64();
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Partially written line
                }
            }

            return info;
        }

        // Workspace folder name from the "Cwd" of a tool call or the "Active Workspaces" header
        private static String ExtractProjectName(List<String> lines)
        {
            var head = lines.Take(35).Where(l => l.Contains("\"Cwd\"") || l.Contains("Active Workspaces"));
            var tail = lines.Skip(Math.Max(0, lines.Count - 20)).Reverse().Where(l => l.Contains("\"Cwd\""));

            foreach (var line in head.Concat(tail))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in calls.EnumerateArray())
                        {
                            if (tc.TryGetProperty("args", out var args) && args.TryGetProperty("Cwd", out var cwd))
                            {
                                var name = FolderName(cwd.GetString());
                                if (name != null) return name;
                            }
                        }
                    }

                    var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    var idx = content.IndexOf("Active Workspaces:", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var rest = content.Substring(idx + "Active Workspaces:".Length);
                        var end = rest.IndexOfAny(new[] { '\r', '\n' });
                        var name = FolderName((end > 0 ? rest.Substring(0, end) : rest).Trim().TrimStart('-'));
                        if (name != null) return name;
                    }
                }
                catch (JsonException)
                {
                }
            }

            return "";
        }

        private static String FolderName(String rawPath)
        {
            var path = (rawPath ?? "").Trim().Trim('"', '\'');
            if (path.Length <= 2) return null;
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            return String.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static String ExtractFirstUserPrompt(String firstLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(firstLine);
                var text = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var start = text.IndexOf("<USER_REQUEST>", StringComparison.Ordinal);
                if (start < 0) return "";

                start += "<USER_REQUEST>".Length;
                var end = text.IndexOf("</USER_REQUEST>", start, StringComparison.Ordinal);
                var request = (end > start ? text.Substring(start, end - start) : text.Substring(start)).Trim();
                var first = request.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return first.Length > 22 ? first.Substring(0, 20) + ".." : first;
            }
            catch (JsonException)
            {
                return "";
            }
        }

        private static String CleanWorkspacePath(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return "";
            var s = raw.Trim().TrimStart('[', '"', '\'').TrimEnd(']', '"', '\'').Trim();
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(7);
            }
            return Uri.UnescapeDataString(s).TrimEnd('/', '\\');
        }


        public void Dispose()
        {
            this._pollTimer?.Dispose();
        }
    }
}
