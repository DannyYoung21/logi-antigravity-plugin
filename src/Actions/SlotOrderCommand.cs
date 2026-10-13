namespace Loupedeck.AntigravityPlugin.Actions
{
    using System;
    using Loupedeck.AntigravityPlugin.Helpers;
    using Loupedeck.AntigravityPlugin.Models;
    using Loupedeck.AntigravityPlugin.Services;

    // Cycles how sessions claim slots: Sticky -> Recency -> Settled (same as the Claude Desktop plugin)
    public class SlotOrderCommand : PluginMultistateDynamicCommand
    {
        public SlotOrderCommand()
            : base(displayName: "Slot Order", description: "Chooses how session tiles are ordered on the keypad", groupName: "SESSION CONTROLS", supportedDevices: DeviceType.All)
        {
            foreach (var strategy in Enum.GetValues<SlotOrderStrategy>())
            {
                this.AddState(strategy.ToString().ToLowerInvariant(), strategy.ToString(), Describe(strategy));
            }
        }

        public static String Describe(SlotOrderStrategy strategy) => strategy switch
        {
            SlotOrderStrategy.Recency => "The session that did something most recently comes first.",
            SlotOrderStrategy.Settled => "The session whose status changed most recently comes first.",
            _ => "A session keeps the slot it first appeared in."
        };

        protected override Boolean OnLoad()
        {
            (this.Plugin as AntigravityPlugin)?.RestoreSlotOrder();
            this.SetCurrentState((Int32)BrainScannerService.Instance.SlotOrder);
            return base.OnLoad();
        }

        protected override void RunCommand(String actionParameter)
        {
            var next = (SlotOrderStrategy)this.IncrementCurrentState();
            BrainScannerService.Instance.SetSlotOrder(next);
            AntigravityPlugin.Instance?.SaveSlotOrder(next);
            this.ActionImageChanged();
        }

        protected override BitmapImage GetCommandImage(String actionParameter, Int32 stateIndex, PluginImageSize imageSize)
        {
            return SessionTileRenderer.RenderSlotOrderKey(stateIndex, imageSize);
        }
    }
}
