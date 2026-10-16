namespace Loupedeck.AntigravityControllerPlugin.Actions
{
    using System;
    using Loupedeck.AntigravityControllerPlugin.Helpers;
    using Loupedeck.AntigravityControllerPlugin.Models;
    using Loupedeck.AntigravityControllerPlugin.Services;

    // One keypad tile per session slot. Pressing it opens a 3x3 page with the session's controls.
    public abstract class SessionSlotDynamicFolder : PluginDynamicFolder
    {
        // Keys inside the folder (position 0 is the native Back button)
        private static readonly String[] DetailKeys =
        {
            "AllowOnce", "ProjectStatus",
            "FiveHourLimit", "WeeklyLimit", "Context",
            "Refresh", "Stop", "FiveHourResetTime"
        };

        private Boolean _isActive;

        public abstract Int32 SlotNumber { get; }

        protected SessionSlotDynamicFolder()
        {
            this.GroupName = "ANTIGRAVITY SESSIONS";

            BrainScannerService.Instance.OnSessionsChanged += this.OnSessionsChanged;
            TileAnimationService.Instance.OnAnimationTick += this.OnAnimationTick;
        }

        private AntigravitySession Session => BrainScannerService.Instance.GetSessionForSlot(this.SlotNumber);

        private void OnSessionsChanged()
        {
            this.RefreshTile();
            if (this._isActive)
            {
                foreach (var key in DetailKeys)
                {
                    this.CommandImageChanged(key);
                }
            }
        }

        // Thinking tiles pulse, so only they are redrawn on every animation frame
        private void OnAnimationTick(Double breath)
        {
            if (this.Session?.State == AgentSessionState.Thinking)
            {
                this.RefreshTile();
                if (this._isActive)
                {
                    this.CommandImageChanged("ProjectStatus");
                }
            }
        }

        // Same notification the Claude Desktop plugin uses to redraw a folder tile on the keypad
        private void RefreshTile() =>
            this.Plugin?.OnActionImageChanged("#DynamicFolder", "DynamicFolder#" + this.GetType().FullName, false);

        public override Boolean Activate()
        {
            this._isActive = true;
            BrainScannerService.Instance.CurrentActiveSlot = this.SlotNumber;
            BrainScannerService.Instance.HoldSlot(this.SlotNumber);
            this.OnSessionsChanged();
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._isActive = false;
            BrainScannerService.Instance.ReleaseSlot(this.SlotNumber);
            return base.Deactivate();
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) => PluginDynamicFolderNavigation.None;

        // Row 0: Back, Allow once, Project status (opens the conversation)
        // Row 1: 5h limit, 7d limit, Context
        // Row 2: Refresh, Stop/Resume, 5h reset time
        public override String[] GetButtonPressActionNames(DeviceType deviceType)
        {
            var names = new String[DetailKeys.Length + 1];
            names[0] = PluginDynamicFolder.NavigateUpActionName;
            for (var i = 0; i < DetailKeys.Length; i++)
            {
                names[i + 1] = this.CreateCommandName(DetailKeys[i]);
            }
            return names;
        }

        public override BitmapImage GetButtonImage(PluginImageSize imageSize) =>
            SessionTileRenderer.RenderSlotTile(this.SlotNumber, this.Session, TileAnimationService.Instance.CurrentBreath, imageSize, isFolder: true);

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var session = this.Session;
            var quota = BrainScannerService.Instance.Quota;

            // Limits of the quota group the session's model belongs to (Gemini, Claude/GPT or Gemini 4)
            var group = session?.ModelGroup ?? QuotaGroup.Gemini;
            var limits = quota.For(group);

            // Gemini 4 has no 5-hour limit: the reset key then counts down the weekly limit instead
            var resetWindow = quota.IsLive && !limits.FiveHour.Available && limits.Weekly.Available ? "7d" : "5h";

            return actionParameter switch
            {
                "AllowOnce" => SessionTileRenderer.RenderAllowOnceKey(session, imageSize),
                "ProjectStatus" => SessionTileRenderer.RenderSlotTile(this.SlotNumber, session, TileAnimationService.Instance.CurrentBreath, imageSize),
                "Context" => SessionTileRenderer.RenderContextKey(session, imageSize),
                "FiveHourLimit" => SessionTileRenderer.RenderLimitKey(group, "5h", limits.FiveHour, quota.IsLive, imageSize),
                "WeeklyLimit" => SessionTileRenderer.RenderLimitKey(group, "7d", limits.Weekly, quota.IsLive, imageSize),
                "FiveHourResetTime" => SessionTileRenderer.RenderResetKey(group, resetWindow,
                    resetWindow == "7d" ? limits.Weekly : limits.FiveHour, quota.IsLive, imageSize),
                "Refresh" => SessionTileRenderer.RenderRefreshKey(imageSize, BrainScannerService.Instance.IsSyncing),
                "Stop" => SessionTileRenderer.RenderStopKey(
                    resume: session?.State == AgentSessionState.Stopped,
                    active: session?.State == AgentSessionState.Thinking || session?.IsWaitingForUser == true,
                    imageSize),
                _ => this.GetButtonImage(imageSize)
            };
        }

        public override void RunCommand(String actionParameter)
        {
            var scanner = BrainScannerService.Instance;
            var session = this.Session;

            switch (actionParameter)
            {
                case "AllowOnce" when session?.State == AgentSessionState.Choose:
                    scanner.OpenSession(session);   // questions are answered in Antigravity
                    break;
                case "AllowOnce" when session != null:
                    scanner.AnswerPermission(session, PermissionDecision.AllowOnce);
                    break;
                case "Stop":
                    scanner.StopOrResume(session);
                    break;
                case "ProjectStatus":
                case "Context":
                    scanner.OpenSession(session);
                    break;
                case "FiveHourLimit":
                case "WeeklyLimit":
                case "FiveHourResetTime":
                case "Refresh":
                    scanner.TriggerManualSync();
                    break;
            }
        }
    }

    // Concrete slot folders for Logi Options+ (class names are part of saved keypad layouts)
    public class SessionSlot01DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 1;
        public SessionSlot01DynamicFolder() { this.DisplayName = "Session Slot 1"; }
    }

    public class SessionSlot02DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 2;
        public SessionSlot02DynamicFolder() { this.DisplayName = "Session Slot 2"; }
    }

    public class SessionSlot03DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 3;
        public SessionSlot03DynamicFolder() { this.DisplayName = "Session Slot 3"; }
    }

    public class SessionSlot04DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 4;
        public SessionSlot04DynamicFolder() { this.DisplayName = "Session Slot 4"; }
    }

    public class SessionSlot05DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 5;
        public SessionSlot05DynamicFolder() { this.DisplayName = "Session Slot 5"; }
    }

    public class SessionSlot06DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 6;
        public SessionSlot06DynamicFolder() { this.DisplayName = "Session Slot 6"; }
    }

    public class SessionSlot07DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 7;
        public SessionSlot07DynamicFolder() { this.DisplayName = "Session Slot 7"; }
    }

    public class SessionSlot08DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 8;
        public SessionSlot08DynamicFolder() { this.DisplayName = "Session Slot 8"; }
    }

    public class SessionSlot09DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 9;
        public SessionSlot09DynamicFolder() { this.DisplayName = "Session Slot 9"; }
    }

    public class SessionSlot10DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 10;
        public SessionSlot10DynamicFolder() { this.DisplayName = "Session Slot 10"; }
    }

    public class SessionSlot11DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 11;
        public SessionSlot11DynamicFolder() { this.DisplayName = "Session Slot 11"; }
    }

    public class SessionSlot12DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 12;
        public SessionSlot12DynamicFolder() { this.DisplayName = "Session Slot 12"; }
    }

    public class SessionSlot13DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 13;
        public SessionSlot13DynamicFolder() { this.DisplayName = "Session Slot 13"; }
    }

    public class SessionSlot14DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 14;
        public SessionSlot14DynamicFolder() { this.DisplayName = "Session Slot 14"; }
    }

    public class SessionSlot15DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 15;
        public SessionSlot15DynamicFolder() { this.DisplayName = "Session Slot 15"; }
    }

    public class SessionSlot16DynamicFolder : SessionSlotDynamicFolder
    {
        public override Int32 SlotNumber => 16;
        public SessionSlot16DynamicFolder() { this.DisplayName = "Session Slot 16"; }
    }
}
