namespace Loupedeck.AntigravityControllerPlugin.Models
{
    using System;
    using Loupedeck.AntigravityControllerPlugin.Services;

    public enum AgentSessionState
    {
        Idle,
        Thinking,
        Complete,
        Error,
        Permission,
        Stopped     // Stopped by the user; can be resumed
    }

    // How sessions claim slots (same model as the Claude Desktop plugin)
    public enum SlotOrderStrategy
    {
        Sticky,   // A session keeps the slot it first appeared in.
        Recency,  // The session that did something most recently comes first.
        Settled   // The session whose status changed most recently comes first.
    }

    // Antigravity has one quota group for Gemini and one for third-party models (Claude, GPT-OSS)
    public enum QuotaGroup
    {
        Gemini,
        ThirdParty
    }

    // The two Antigravity products; each runs its own language server and keeps its own data folder
    public enum AntigravityHost
    {
        App,    // Antigravity.app (agent manager), ~/.gemini/antigravity
        Ide     // Antigravity IDE.app, ~/.gemini/antigravity-ide
    }

    // Answers to a pending permission request (same choices as the Antigravity UI)
    public enum PermissionDecision
    {
        AllowOnce,
        AllowConversation,
        Deny
    }

    public class AntigravitySession
    {
        private const Int64 DefaultContextWindow = 1_048_576;

        public String Id { get; set; } = String.Empty;
        public String DisplayName { get; set; } = String.Empty;
        public AntigravityHost Host { get; set; } = AntigravityHost.App;
        public AgentSessionState State { get; set; } = AgentSessionState.Idle;
        public DateTime LastUpdated { get; set; } = DateTime.MinValue;
        public DateTime StatusSince { get; set; } = DateTime.MinValue;
        public String LiveCascadeId { get; set; } = String.Empty;
        public String WorkspaceUri { get; set; } = String.Empty;

        public Int64 InputTokens { get; set; }
        public Int64 OutputTokens { get; set; }
        public Int64 CacheReadTokens { get; set; }

        // Exact context usage as reported by Antigravity for the model in use (0 = unknown)
        public Int64 ContextUsedTokens { get; set; }
        public Int64 ContextMaxTokens { get; set; }

        // Quota group of the model the session used last (drives the limit keys inside its folder)
        public QuotaGroup ModelGroup { get; set; } = QuotaGroup.Gemini;

        public Int64 TotalContextTokens => this.ContextUsedTokens > 0
            ? this.ContextUsedTokens
            : this.InputTokens + this.CacheReadTokens + this.OutputTokens;

        public Double ContextUsagePercent
        {
            get
            {
                var max = this.ContextMaxTokens > 0 ? this.ContextMaxTokens : DefaultContextWindow;
                return Math.Clamp((Double)this.TotalContextTokens / max * 100.0, 0.0, 100.0);
            }
        }

        // Slots are claimed per project (one tile per project and product)
        public String SlotKey => this.Host == AntigravityHost.Ide ? this.DisplayName + " (IDE)" : this.DisplayName;

        // Cascade to talk to for Allow Once / Stop / Resume
        public String CascadeId => !String.IsNullOrWhiteSpace(this.LiveCascadeId) ? this.LiveCascadeId : this.Id;

        public String StatusPillText => this.State switch
        {
            AgentSessionState.Thinking => "Thinking",
            AgentSessionState.Complete => "Complete",
            AgentSessionState.Permission => "Permission",
            AgentSessionState.Error => "Error",
            AgentSessionState.Stopped => "Stopped",
            _ => "Idle"
        };
    }

    public class QuotaWindow
    {
        public Double UsedPercent { get; set; }
        public DateTime? ResetTimeUtc { get; set; }

        public TimeSpan? RemainingUntilReset => this.ResetTimeUtc.HasValue
            ? TimeSpan.FromTicks(Math.Max(0, (this.ResetTimeUtc.Value - DateTime.UtcNow).Ticks))
            : null;
    }

    public class QuotaGroupSummary
    {
        public QuotaWindow FiveHour { get; set; } = new();
        public QuotaWindow Weekly { get; set; } = new();
    }

    public class QuotaSummary
    {
        public QuotaGroupSummary Gemini { get; set; } = new();
        public QuotaGroupSummary ThirdParty { get; set; } = new();

        // False until Antigravity answered once; tiles then show placeholders instead of 0%
        public Boolean IsLive { get; set; }

        public QuotaGroupSummary For(QuotaGroup group) => group == QuotaGroup.ThirdParty ? this.ThirdParty : this.Gemini;
    }

    public class LiveTrajectoryInfo
    {
        public String CascadeId { get; set; } = String.Empty;
        public String Summary { get; set; } = String.Empty;
        public String WorkspacePath { get; set; } = String.Empty;
        public String Status { get; set; } = String.Empty;
        public String TerminationReason { get; set; } = String.Empty;
        public Int32 StepCount { get; set; }
        public Int32 LastUserInputStepIndex { get; set; } = -1;
        public String LastModified { get; set; } = String.Empty;
        public LanguageServerConnection Server { get; set; }

        public AntigravityHost Host => this.Server?.Host ?? AntigravityHost.App;
        public Boolean IsRunning => this.Status.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                                    this.Status.Contains("BUSY", StringComparison.OrdinalIgnoreCase);
        public Boolean WasCanceled => this.TerminationReason.Contains("USER_CANCELED", StringComparison.OrdinalIgnoreCase);
    }

    public class LiveTrajectoryDetails
    {
        public Int64 InputTokens { get; set; }
        public Int64 OutputTokens { get; set; }
        public Int64 CacheReadTokens { get; set; }
        public Int64 ContextUsedTokens { get; set; }
        public Int64 ContextMaxTokens { get; set; }
        public QuotaGroup? ModelGroup { get; set; }
        public Boolean IsWaitingForUser { get; set; }

        // The last response was cut off by the user (Antigravity IDE does not report a termination reason)
        public Boolean LastResponseCanceled { get; set; }
    }
}
