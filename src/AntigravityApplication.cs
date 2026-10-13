namespace Loupedeck.AntigravityPlugin
{
    using System;

    // Required by the Logi Plugin Service loader even though the plugin is not tied to an application (HasNoApplication)
    public class AntigravityApplication : ClientApplication
    {
        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
