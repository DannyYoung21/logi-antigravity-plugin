namespace Loupedeck.AntigravityControllerPlugin.Actions
{
    using System;
    using Loupedeck.AntigravityControllerPlugin.Helpers;
    using Loupedeck.AntigravityControllerPlugin.Models;
    using Loupedeck.AntigravityControllerPlugin.Services;

    // Standalone keys. Class names are part of saved keypad layouts, so they must not be renamed.

    // Redraws whenever sessions or quota change
    public abstract class LiveCommand : PluginDynamicCommand
    {
        protected const String SessionGroup = "SESSION CONTROLS";

        protected LiveCommand(String displayName, String description, String groupName)
            : base(displayName: displayName, description: description, groupName: groupName)
        {
            BrainScannerService.Instance.OnSessionsChanged += () => this.ActionImageChanged();
        }

        protected static BrainScannerService Scanner => BrainScannerService.Instance;
    }

    public class ContextWindowCommand : LiveCommand
    {
        public ContextWindowCommand()
            : base("Context", "Context window usage of the last opened slot (exact window of the model in use)", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderContextKey(Scanner.GetActiveSession(), imageSize);

        protected override void RunCommand(String actionParameter) => Scanner.OpenSession(Scanner.GetActiveSession());
    }

    public class TokenUsageCommand : LiveCommand
    {
        public TokenUsageCommand()
            : base("Tokens", "Token usage (input, output, cache) of the last opened slot", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderTokenStatsKey(Scanner.GetActiveSession(), imageSize);

        protected override void RunCommand(String actionParameter) => Scanner.TriggerManualSync();
    }

    // ---------------------------------------------------------------- Permission answers

    public class AllowOnceCommand : LiveCommand
    {
        public AllowOnceCommand()
            : base("Allow Once", "Approves the pending action once (last opened slot first, else any waiting session)", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderAllowOnceKey(Scanner.GetPendingSession(), imageSize);

        // Approve the session that is actually waiting; if none is known, answer every waiting cascade
        protected override void RunCommand(String actionParameter) =>
            Scanner.AnswerPermission(Scanner.GetPendingSession(), PermissionDecision.AllowOnce);
    }

    public class AllowConversationCommand : LiveCommand
    {
        public AllowConversationCommand()
            : base("Allow in Conversation", "Approves the pending action for the rest of this conversation (like \"Allow in Conversation\" in Antigravity)", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderAllowConversationKey(Scanner.GetPendingSession(), imageSize);

        protected override void RunCommand(String actionParameter)
        {
            var session = Scanner.GetPendingSession();
            if (session != null)
            {
                Scanner.AnswerPermission(session, PermissionDecision.AllowConversation);
            }
        }
    }

    public class DenyCommand : LiveCommand
    {
        public DenyCommand()
            : base("Deny", "Rejects the pending action (last opened slot first, else the session waiting longest)", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderDenyKey(Scanner.GetPendingSession(), imageSize);

        protected override void RunCommand(String actionParameter)
        {
            var session = Scanner.GetPendingSession();
            if (session != null)
            {
                Scanner.AnswerPermission(session, PermissionDecision.Deny);
            }
        }
    }

    // Pulses while sessions wait; each press shows the next waiting session in Antigravity
    public class NextWaitingCommand : LiveCommand
    {
        public NextWaitingCommand()
            : base("Next Waiting Session", "Lights up while a session waits for permission; press to jump to it (repeated presses cycle)", SessionGroup)
        {
            TileAnimationService.Instance.OnAnimationTick += _ =>
            {
                if (Scanner.GetWaitingSessions().Count > 0)
                {
                    this.ActionImageChanged();
                }
            };
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderNextWaitingKey(Scanner.GetWaitingSessions(), TileAnimationService.Instance.CurrentBreath, imageSize);

        protected override void RunCommand(String actionParameter) => Scanner.ShowNextWaitingSession();
    }

    // ---------------------------------------------------------------- Session controls

    // Stops every running agent; turns into Resume once they are stopped
    public class StopCommand : LiveCommand
    {
        public StopCommand()
            : base("Stop / Resume", "Stops every running agent; afterwards the same key resumes them", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderStopKey(Scanner.GlobalKeyResumes, active: true, imageSize);

        protected override void RunCommand(String actionParameter) => Scanner.StopOrResumeAll();
    }

    public class NewConversationCommand : PluginDynamicCommand
    {
        public NewConversationCommand()
            : base(displayName: "New Conversation", description: "Starts a new conversation in Antigravity (or Antigravity IDE, if the last opened slot belongs to it)", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderNewConversationKey(imageSize);

        protected override void RunCommand(String actionParameter) => BrainScannerService.Instance.StartNewConversation();
    }

    public class FocusAntigravityCommand : PluginDynamicCommand
    {
        public FocusAntigravityCommand()
            : base(displayName: "Open Antigravity", description: "Brings Antigravity (or Antigravity IDE) to the foreground", groupName: "SESSION CONTROLS")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderFocusKey(imageSize);

        protected override void RunCommand(String actionParameter) => BrainScannerService.Instance.FocusAntigravityWindow();
    }

    public class RefreshSessionsCommand : LiveCommand
    {
        public RefreshSessionsCommand()
            : base("Refresh", "Refreshes sessions and limits now", SessionGroup)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            SessionTileRenderer.RenderRefreshKey(imageSize, Scanner.IsSyncing);

        protected override void RunCommand(String actionParameter) => Scanner.TriggerManualSync();
    }

    // ---------------------------------------------------------------- Quota

    // Antigravity quota groups: "Gemini Models" and "Claude and GPT models" (third party)
    public abstract class QuotaCommand : LiveCommand
    {
        public enum Kind { FiveHour, Weekly, FiveHourReset, WeeklyReset }

        private readonly QuotaGroup _group;
        private readonly Kind _kind;

        protected QuotaCommand(QuotaGroup group, Kind kind, String displayName, String description)
            : base(displayName, description, group == QuotaGroup.Gemini ? "LIMITS: GEMINI" : "LIMITS: THIRD-PARTY (CLAUDE & GPT)")
        {
            this._group = group;
            this._kind = kind;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var quota = Scanner.Quota;
            var windows = quota.For(this._group);
            return this._kind switch
            {
                Kind.Weekly => SessionTileRenderer.RenderLimitKey(this._group, "7d", windows.Weekly, quota.IsLive, imageSize),
                Kind.FiveHourReset => SessionTileRenderer.RenderResetKey(this._group, "5h", windows.FiveHour, quota.IsLive, imageSize),
                Kind.WeeklyReset => SessionTileRenderer.RenderResetKey(this._group, "7d", windows.Weekly, quota.IsLive, imageSize),
                _ => SessionTileRenderer.RenderLimitKey(this._group, "5h", windows.FiveHour, quota.IsLive, imageSize)
            };
        }

        protected override void RunCommand(String actionParameter) => Scanner.TriggerManualSync();
    }

    public class FiveHourLimitCommand : QuotaCommand
    {
        public FiveHourLimitCommand()
            : base(QuotaGroup.Gemini, Kind.FiveHour, "Gemini 5h Limit", "Used share of the rolling 5-hour limit for Gemini models")
        {
        }
    }

    public class WeeklyLimitCommand : QuotaCommand
    {
        public WeeklyLimitCommand()
            : base(QuotaGroup.Gemini, Kind.Weekly, "Gemini 7d Limit", "Used share of the weekly limit for Gemini models")
        {
        }
    }

    public class FiveHourResetTimeCommand : QuotaCommand
    {
        public FiveHourResetTimeCommand()
            : base(QuotaGroup.Gemini, Kind.FiveHourReset, "Gemini 5h Reset Time", "Time until the Gemini 5-hour limit resets")
        {
        }
    }

    public class WeeklyResetTimeCommand : QuotaCommand
    {
        public WeeklyResetTimeCommand()
            : base(QuotaGroup.Gemini, Kind.WeeklyReset, "Gemini 7d Reset Time", "Time until the Gemini weekly limit resets")
        {
        }
    }

    public class ThirdPartyFiveHourLimitCommand : QuotaCommand
    {
        public ThirdPartyFiveHourLimitCommand()
            : base(QuotaGroup.ThirdParty, Kind.FiveHour, "Third-Party 5h Limit (Claude/GPT)", "Used share of the rolling 5-hour limit for third-party models (Claude, GPT-OSS)")
        {
        }
    }

    public class ThirdPartyWeeklyLimitCommand : QuotaCommand
    {
        public ThirdPartyWeeklyLimitCommand()
            : base(QuotaGroup.ThirdParty, Kind.Weekly, "Third-Party 7d Limit (Claude/GPT)", "Used share of the weekly limit for third-party models (Claude, GPT-OSS)")
        {
        }
    }

    public class ThirdPartyFiveHourResetTimeCommand : QuotaCommand
    {
        public ThirdPartyFiveHourResetTimeCommand()
            : base(QuotaGroup.ThirdParty, Kind.FiveHourReset, "Third-Party 5h Reset Time (Claude/GPT)", "Time until the third-party 5-hour limit resets")
        {
        }
    }

    public class ThirdPartyWeeklyResetTimeCommand : QuotaCommand
    {
        public ThirdPartyWeeklyResetTimeCommand()
            : base(QuotaGroup.ThirdParty, Kind.WeeklyReset, "Third-Party 7d Reset Time (Claude/GPT)", "Time until the third-party weekly limit resets")
        {
        }
    }
}
