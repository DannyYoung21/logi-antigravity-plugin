namespace Loupedeck.AntigravityControllerPlugin.Services
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Loupedeck.AntigravityControllerPlugin.Models;

    // Slow, calm pulse for Thinking tiles (and the waiting indicator). Runs only while something animates.
    public class TileAnimationService : IDisposable
    {
        private static readonly Lazy<TileAnimationService> _instance = new(() => new TileAnimationService());
        public static TileAnimationService Instance => _instance.Value;

        private const Double PulsePeriodSeconds = 3.2;  // one breath: brighten and dim again
        private const Int32 FrameMilliseconds = 60;

        private readonly Timer _animationTimer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Boolean _isRunning;

        public event Action<Double> OnAnimationTick;

        // 0.0 = darkest, 1.0 = brightest
        public Double CurrentBreath { get; private set; }

        public TileAnimationService()
        {
            this._animationTimer = new Timer(this.Tick, null, Timeout.Infinite, Timeout.Infinite);

            // Start/stop the loop as sessions start and stop thinking or waiting
            BrainScannerService.Instance.OnSessionsChanged += this.CheckAnimationNeed;
            this.CheckAnimationNeed();
        }

        private void CheckAnimationNeed()
        {
            var animate = BrainScannerService.Instance.Sessions.Any(s =>
                s.State == AgentSessionState.Thinking || s.IsWaitingForUser);

            if (animate && !this._isRunning)
            {
                this._isRunning = true;
                this._animationTimer.Change(0, FrameMilliseconds);
            }
            else if (!animate && this._isRunning)
            {
                this._isRunning = false;
                this._animationTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void Tick(Object state)
        {
            // Based on elapsed time, so a late timer tick never makes the pulse stutter or speed up.
            // Cosine eases in and out at both ends like a calm heartbeat.
            var phase = this._clock.Elapsed.TotalSeconds / PulsePeriodSeconds * 2.0 * Math.PI;
            this.CurrentBreath = 0.5 - 0.5 * Math.Cos(phase);

            this.OnAnimationTick?.Invoke(this.CurrentBreath);
        }

        public void Dispose()
        {
            this._animationTimer?.Dispose();
        }
    }
}
