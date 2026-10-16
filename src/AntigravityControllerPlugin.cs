namespace Loupedeck.AntigravityControllerPlugin
{
    using System;
    using Loupedeck.AntigravityControllerPlugin.Models;
    using Loupedeck.AntigravityControllerPlugin.Services;

    public class AntigravityControllerPlugin : Plugin
    {
        private const String SlotOrderSetting = "slotOrder";

        public static AntigravityControllerPlugin Instance { get; private set; }

        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        public AntigravityControllerPlugin()
        {
            Instance = this;
            PluginLog.Init(this.Log);
        }

        public override void Load()
        {
            PluginLog.Info("Antigravity Controller is loading...");
            _ = BrainScannerService.Instance;
            _ = TileAnimationService.Instance;
            this.RestoreSlotOrder();
        }

        public override void Unload()
        {
            PluginLog.Info("Antigravity Controller unloading...");
            BrainScannerService.Instance.Dispose();
            TileAnimationService.Instance.Dispose();
        }

        // Actions load before Load(), so whoever comes first restores the saved slot order
        public void RestoreSlotOrder()
        {
            if (this.TryGetPluginSetting(SlotOrderSetting, out var savedOrder) &&
                Enum.TryParse<SlotOrderStrategy>(savedOrder, ignoreCase: true, out var strategy))
            {
                BrainScannerService.Instance.SetSlotOrder(strategy);
            }
        }

        public void SaveSlotOrder(SlotOrderStrategy strategy) =>
            this.SetPluginSetting(SlotOrderSetting, strategy.ToString().ToLowerInvariant(), false);
    }
}
