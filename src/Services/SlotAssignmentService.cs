namespace Loupedeck.AntigravityControllerPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Loupedeck.AntigravityControllerPlugin.Models;

    // Maps sessions to keypad slots. Mirrors the Claude Desktop plugin:
    //  - Sticky:  a session keeps the slot it first appeared in; slots free up when a session disappears.
    //  - Recency: re-sorted on every sync, most recently active first.
    //  - Settled: re-sorted only when some session's status changes, most recent status change first.
    // In Recency/Settled a session waiting for permission always comes first, and the session whose
    // folder is currently open stays where it is so the keys don't move under your finger.
    public class SlotAssignmentService
    {
        public const Int32 MaxSlots = 16;

        private Dictionary<Int32, String> _slots = new();
        private Dictionary<String, AgentSessionState> _lastStatuses = new(StringComparer.OrdinalIgnoreCase);
        private Boolean _forceResort;

        public String GetKey(Int32 slot) => this._slots.TryGetValue(slot, out var key) ? key : null;

        public Int32? GetSlot(String key)
        {
            if (String.IsNullOrEmpty(key)) return null;
            foreach (var kvp in this._slots)
            {
                if (String.Equals(kvp.Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        public void RequestResort() => this._forceResort = true;

        public void Sync(IReadOnlyList<AntigravitySession> sessions, SlotOrderStrategy strategy, String heldKey)
        {
            var resort = strategy switch
            {
                SlotOrderStrategy.Recency => true,
                SlotOrderStrategy.Settled => this._forceResort || this.StatusesChanged(sessions),
                _ => false
            };
            this._forceResort = false;
            this.RecordStatuses(sessions);

            this._slots = resort
                ? this.Resort(sessions, strategy, heldKey)
                : this.KeepSticky(sessions);
        }

        private Dictionary<Int32, String> KeepSticky(IReadOnlyList<AntigravitySession> sessions)
        {
            var present = new HashSet<String>(sessions.Select(s => s.SlotKey), StringComparer.OrdinalIgnoreCase);
            var slots = new Dictionary<Int32, String>();

            foreach (var kvp in this._slots)
            {
                if (present.Contains(kvp.Value))
                {
                    slots[kvp.Key] = kvp.Value;
                }
                else
                {
                    PluginLog.Info($"[SLOTS] Vacated slot {kvp.Key}: {kvp.Value} left");
                }
            }

            var seated = new HashSet<String>(slots.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions)
            {
                if (seated.Contains(s.SlotKey)) continue;

                var free = Enumerable.Range(1, MaxSlots).FirstOrDefault(n => !slots.ContainsKey(n));
                if (free == 0) break;

                PluginLog.Info($"[SLOTS] Seated {s.SlotKey} in slot {free}");
                slots[free] = s.SlotKey;
                seated.Add(s.SlotKey);
            }

            return slots;
        }

        private Dictionary<Int32, String> Resort(IReadOnlyList<AntigravitySession> sessions, SlotOrderStrategy strategy, String heldKey)
        {
            var ordered = sessions.OrderByDescending(s => s.State == AgentSessionState.Permission);
            ordered = strategy == SlotOrderStrategy.Settled
                ? ordered.ThenByDescending(s => s.StatusSince)
                : ordered.ThenByDescending(s => s.LastUpdated);
            var list = ordered.ThenBy(s => s.SlotKey, StringComparer.Ordinal).ToList();

            var slots = new Dictionary<Int32, String>();

            // Keep the open folder's session in its slot
            var heldSlot = this.GetSlot(heldKey);
            var held = heldSlot.HasValue && list.Any(s => String.Equals(s.SlotKey, heldKey, StringComparison.OrdinalIgnoreCase));
            if (held)
            {
                slots[heldSlot.Value] = heldKey;
            }

            var next = 1;
            foreach (var s in list)
            {
                if (held && String.Equals(s.SlotKey, heldKey, StringComparison.OrdinalIgnoreCase)) continue;

                while (next <= MaxSlots && slots.ContainsKey(next)) next++;
                if (next > MaxSlots) break;

                slots[next] = s.SlotKey;
                next++;
            }

            return slots;
        }

        private Boolean StatusesChanged(IReadOnlyList<AntigravitySession> sessions)
        {
            if (sessions.Count != this._lastStatuses.Count) return true;
            foreach (var s in sessions)
            {
                if (!this._lastStatuses.TryGetValue(s.SlotKey, out var last) || last != s.State)
                {
                    return true;
                }
            }
            return false;
        }

        private void RecordStatuses(IReadOnlyList<AntigravitySession> sessions)
        {
            var statuses = new Dictionary<String, AgentSessionState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions)
            {
                statuses[s.SlotKey] = s.State;
            }
            this._lastStatuses = statuses;
        }
    }
}
