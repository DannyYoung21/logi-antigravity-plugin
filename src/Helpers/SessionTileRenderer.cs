namespace Loupedeck.AntigravityControllerPlugin.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Loupedeck.AntigravityControllerPlugin.Models;

    // Draws every key image. Layouts are computed from the actual key size and centered as a block,
    // so they look right on 116 px (MX Creative Keypad) as well as 80 px keys.
    public static class SessionTileRenderer
    {
        // Colors: Gemini signature Navy Blue and Emerald Green
        public static readonly BitmapColor TileBaseColor = new(36, 55, 86);       // Gemini Navy Blue (#243756)
        public static readonly BitmapColor TileCompleteColor = new(22, 163, 74);  // Vibrant Emerald Green (#16A34A)
        public static readonly BitmapColor TilePermissionColor = new(234, 179, 8); // Vibrant Amber / Yellow (#EAB308)
        public static readonly BitmapColor TileErrorColor = new(185, 28, 28);     // Error Red (#B91C1C)
        public static readonly BitmapColor TextWhite = new(255, 255, 255);
        public static readonly BitmapColor TextDim = new(190, 210, 235);
        public static readonly BitmapColor TextMuted = new(130, 160, 195);
        public static readonly BitmapColor TextDark = new(24, 24, 27);
        public static readonly BitmapColor TrackColor = new(20, 32, 52);          // Darkened Navy Track for Ring
        public static readonly BitmapColor AccentGreen = new(34, 197, 94);
        public static readonly BitmapColor AccentCyan = new(56, 189, 248);
        public static readonly BitmapColor AccentRed = new(239, 68, 68);
        public static readonly BitmapColor RingContextColor = new(255, 255, 255);

        // Thinking: the tile glows orange, slowly brighter and darker
        private static readonly BitmapColor PulseDim = new(168, 70, 12);
        private static readonly BitmapColor PulseBright = new(244, 128, 28);

        // Gauge color for used quota: green <50%, yellow 50-80%, red >80%
        public static BitmapColor GetUsageRingColor(Double percent) =>
            percent < 50.0 ? AccentGreen : percent < 80.0 ? new BitmapColor(234, 179, 8) : AccentRed;

        public static String GroupLabel(QuotaGroup group) => group == QuotaGroup.ThirdParty ? "Claude/GPT" : "Gemini";

        // ---------------------------------------------------------------- Session tile

        public static BitmapImage RenderSlotTile(Int32 slotNumber, AntigravitySession session, Double breath, PluginImageSize imageSize, Boolean isFolder = false)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            var large = h >= 100;

            // On the main page the device draws a folder tab at the top; inside the folder we draw a small notch
            var topMargin = isFolder ? (large ? 14 : 10) : 6;

            if (session == null)
            {
                // Same color as every other tile, only the text says it is free
                bb.Clear(TileBaseColor);
                if (!isFolder)
                {
                    bb.FillRectangle((w - 32) / 2, 0, 32, 2, new BitmapColor(60, 80, 110));
                }
                var emptyTop = topMargin + CenterTop(h - topMargin, 44);
                bb.DrawText($"Slot {slotNumber}", 0, emptyTop, w, 22, TextDim, 17);
                bb.DrawText("Empty", 0, emptyTop + 22, w, 22, TextMuted, 16);
                return bb.ToImage();
            }

            var state = session.State;
            bb.Clear(state switch
            {
                AgentSessionState.Thinking => Blend(PulseDim, PulseBright, breath),
                AgentSessionState.Complete => TileCompleteColor,
                AgentSessionState.Permission => TilePermissionColor,
                AgentSessionState.Error => TileErrorColor,
                _ => TileBaseColor
            });

            var textColor = state == AgentSessionState.Permission ? TextDark : TextWhite;
            if (!isFolder)
            {
                bb.FillRectangle((w - 32) / 2, 0, 32, 2, textColor == TextDark ? new BitmapColor(80, 60, 10) : TextWhite);
            }

            // Status pill at the bottom
            var pillH = large ? 24 : 18;
            var pillW = w - (large ? 16 : 8);
            var pillY = h - pillH - (large ? 7 : 3);
            var pillColor = state switch
            {
                AgentSessionState.Thinking => new BitmapColor(92, 38, 6),
                AgentSessionState.Permission => new BitmapColor(38, 30, 10),
                AgentSessionState.Complete => new BitmapColor(15, 118, 54),
                AgentSessionState.Error => new BitmapColor(220, 38, 38),
                AgentSessionState.Stopped => new BitmapColor(100, 116, 139),
                _ => new BitmapColor(48, 72, 110)
            };
            FillRoundedRect(bb, (w - pillW) / 2, pillY, pillW, pillH, pillH / 2, pillColor);
            bb.DrawText(session.StatusPillText, (w - pillW) / 2, pillY + 1, pillW, pillH - 1, TextWhite, large ? 16 : 12);

            // Project name only: one or two lines, centered as a block in the space above the pill
            var (lines, nameFont) = FitName(session.DisplayName, w, large);
            var lineH = nameFont + 4;
            var areaH = pillY - 2 - topMargin;
            var y = topMargin + CenterTop(areaH, lines.Count * lineH);
            foreach (var line in lines)
            {
                bb.DrawText(line, 2, y, w - 4, lineH, textColor, nameFont);
                y += lineH;
            }

            return bb.ToImage();
        }

        // Largest font that fits the name on at most two lines (one line is preferred while the font
        // shrinks by at most 2 pt); still too long -> the second line is shortened
        private static (List<String> Lines, Int32 Font) FitName(String name, Int32 width, Boolean large)
        {
            var sizes = large ? new[] { 18, 17, 16, 15, 14 } : new[] { 15, 14, 13, 12 };
            foreach (var size in sizes.Take(3))
            {
                var single = WrapText(name, MaxChars(width, size));
                if (single.Count == 1)
                {
                    return (single, size);
                }
            }
            foreach (var size in sizes)
            {
                var lines = WrapText(name, MaxChars(width, size));
                if (lines.Count <= 2)
                {
                    return (lines, size);
                }
            }

            var smallest = sizes[^1];
            var max = MaxChars(width, smallest);
            var first = WrapText(name, max)[0];

            // The rest of the name exactly as written (no extra spaces where a word was split)
            var pos = 0;
            foreach (var ch in first)
            {
                while (pos < name.Length && name[pos] != ch) pos++;
                pos++;
            }
            var second = name.Substring(Math.Min(pos, name.Length)).Trim();
            if (second.Length > max)
            {
                second = second.Substring(0, max - 1).TrimEnd() + "…";
            }
            return (new List<String> { first, second }, smallest);
        }

        // Characters that fit on one line (average glyph width is a bit more than half the font size)
        private static Int32 MaxChars(Int32 width, Int32 fontSize) => Math.Max(4, (Int32)((width - 8) / (fontSize * 0.57)));

        // ---------------------------------------------------------------- Gauges

        public static BitmapImage RenderContextKey(AntigravitySession session, PluginImageSize imageSize)
        {
            var totalTokens = session?.TotalContextTokens ?? 0;
            var tokStr = totalTokens >= 1_000_000
                ? $"{totalTokens / 1_000_000.0:0.#}M"
                : (totalTokens >= 1000 ? $"{totalTokens / 1000}k" : $"{totalTokens}");

            return RenderGauge(imageSize, "Context", tokStr, session?.ContextUsagePercent ?? 0.0, RingContextColor);
        }

        // Used share of a quota window
        public static BitmapImage RenderLimitKey(QuotaGroup group, String window, QuotaWindow quota, Boolean isLive, PluginImageSize imageSize)
        {
            var title = $"{GroupLabel(group)} {window}";
            return isLive
                ? RenderGauge(imageSize, title, $"{quota.UsedPercent:0}%", quota.UsedPercent, GetUsageRingColor(quota.UsedPercent))
                : RenderGauge(imageSize, title, "--", 0, TrackColor);
        }

        private static BitmapImage RenderGauge(PluginImageSize imageSize, String title, String value, Double percent, BitmapColor ringColor)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            const Int32 titleH = 18, valueH = 24, gap = 4, stroke = 4;
            var radius = Math.Clamp((h - titleH - valueH - gap - 2 * stroke) / 2, 10, 20);
            var top = CenterTop(h, titleH + valueH + gap + 2 * (radius + stroke));

            bb.DrawText(title, 0, top, w, titleH, TextWhite, TitleFont(title, w));
            bb.DrawText(value, 0, top + titleH, w, valueH, TextWhite, w >= 100 ? 22 : 19);

            var cx = w / 2;
            var cy = top + titleH + valueH + gap + stroke + radius;
            bb.DrawArc(cx, cy, radius, 0, 360, TrackColor, stroke + 1);
            var sweep = (Single)(Math.Clamp(percent, 0.0, 100.0) * 3.6);
            if (sweep > 0)
            {
                bb.DrawArc(cx, cy, radius, -90, sweep, ringColor, stroke);
            }

            return bb.ToImage();
        }

        // Countdown to the reset of a quota window ("5h" or "7d")
        public static BitmapImage RenderResetKey(QuotaGroup group, String window, QuotaWindow quota, Boolean isLive, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            // Header / time / caption / bar, centered as one block
            const Int32 titleH = 18, valueH = 24, captionH = 16, gap = 8, barH = 11;
            var top = CenterTop(h, titleH + valueH + captionH + gap + barH);
            var valueY = top + titleH;
            var captionY = valueY + valueH;
            var barY = captionY + captionH + gap;
            var barMargin = Math.Max(10, (w - 80) / 2);
            var barW = w - 2 * barMargin;
            var windowMinutes = window == "7d" ? 7 * 24 * 60.0 : 5 * 60.0;

            var title = $"{GroupLabel(group)} {window}";
            bb.DrawText(title, 0, top, w, titleH, TextWhite, TitleFont(title, w));

            var remaining = quota.RemainingUntilReset;
            if (!isLive)
            {
                bb.DrawText("--:--", 0, valueY, w, valueH, TextWhite, 18);
                bb.DrawText("Loading...", 0, captionY, w, captionH, TextDim, 13);
                bb.FillRectangle(barMargin, barY, barW, barH, TrackColor);
            }
            else if (quota.UsedPercent <= 0.0 || remaining == null || remaining.Value.TotalMinutes <= 0)
            {
                bb.DrawText("Ready", 0, valueY, w, valueH, AccentGreen, 20);
                bb.DrawText("100% Free", 0, captionY, w, captionH, TextDim, 13);
                bb.FillRectangle(barMargin, barY, barW, barH, AccentGreen);
            }
            else
            {
                var ts = remaining.Value;
                var timeStr = ts.TotalDays >= 1.0 ? $"{(Int32)ts.TotalDays}d {ts.Hours}h"
                    : ts.TotalHours >= 1.0 ? $"{(Int32)ts.TotalHours}h {ts.Minutes:D2}m"
                    : $"{ts.Minutes}m";
                bb.DrawText(timeStr, 0, valueY, w, valueH, TextWhite, ts.TotalHours >= 1.0 ? 17 : 21);
                bb.DrawText("to Reset", 0, captionY, w, captionH, TextDim, 13);

                // Elapsed part of the window
                var elapsed = Math.Clamp(1.0 - ts.TotalMinutes / windowMinutes, 0.0, 1.0);
                bb.FillRectangle(barMargin, barY, barW, barH, TrackColor);
                var filled = (Int32)(barW * elapsed);
                if (filled > 0)
                {
                    bb.FillRectangle(barMargin, barY, filled, barH, AccentCyan);
                }
            }

            return bb.ToImage();
        }

        // ---------------------------------------------------------------- Controls

        // Stop (square) while something runs; Resume (play) once it was stopped
        public static BitmapImage RenderStopKey(Boolean resume, Boolean active, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            const Int32 iconSize = 22, gap = 10, textH = 20;
            var top = CenterTop(h, iconSize + gap + textH);
            var x = (w - iconSize) / 2;

            if (resume)
            {
                // Filled play triangle, drawn column by column (wide on the left, tip on the right)
                for (var i = 0; i <= iconSize; i++)
                {
                    var half = i / 2;
                    bb.DrawLine(x + 2 + i, top + half, x + 2 + i, top + iconSize - half, AccentGreen, 1.5f);
                }
                bb.DrawText("Resume", 0, top + iconSize + gap, w, textH, TextWhite, 18);
            }
            else
            {
                var color = active ? TextWhite : TextMuted;
                bb.DrawLine(x, top, x + iconSize, top, color, 4);
                bb.DrawLine(x + iconSize, top, x + iconSize, top + iconSize, color, 4);
                bb.DrawLine(x + iconSize, top + iconSize, x, top + iconSize, color, 4);
                bb.DrawLine(x, top + iconSize, x, top, color, 4);
                bb.DrawText("Stop", 0, top + iconSize + gap, w, textH, color, 18);
            }

            return bb.ToImage();
        }

        public static BitmapImage RenderAllowOnceKey(AntigravitySession session, PluginImageSize imageSize) =>
            RenderAnswerKey(session?.State == AgentSessionState.Permission, "Allow", "Once", imageSize,
                (bb, cx, cy, color) => DrawCheckMark(bb, cx, cy, color, 3.5f));

        public static BitmapImage RenderAllowConversationKey(AntigravitySession session, PluginImageSize imageSize) =>
            RenderAnswerKey(session?.State == AgentSessionState.Permission, "Allow", "in Chat", imageSize,
                (bb, cx, cy, color) =>
                {
                    DrawCheckMark(bb, cx - 9, cy, color, 3f, 0.62);
                    DrawCheckMark(bb, cx + 9, cy, color, 3f, 0.62);
                });

        public static BitmapImage RenderDenyKey(AntigravitySession session, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            var isPending = session?.State == AgentSessionState.Permission;

            const Int32 lineH = 22, gap = 8, crossH = 16;
            var top = CenterTop(h, lineH + gap + crossH);
            bb.Clear(isPending ? TileErrorColor : TileBaseColor);
            bb.DrawText("Deny", 0, top, w, lineH, TextWhite, 18);

            var cx = w / 2;
            var cy = top + lineH + gap + crossH / 2;
            var color = isPending ? TextWhite : AccentRed;
            bb.DrawLine(cx - 8, cy - 8, cx + 8, cy + 8, color, 3.5f);
            bb.DrawLine(cx - 8, cy + 8, cx + 8, cy - 8, color, 3.5f);

            return bb.ToImage();
        }

        // Shared look of the approval keys: amber while a session waits
        private static BitmapImage RenderAnswerKey(Boolean isPending, String line1, String line2, PluginImageSize imageSize,
            Action<BitmapBuilder, Int32, Int32, BitmapColor> drawIcon)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;

            const Int32 line1H = 22, line2H = 20, gap = 6, iconH = 16;
            var top = CenterTop(h, line1H + line2H + gap + iconH);
            var cx = w / 2;
            var cy = top + line1H + line2H + gap + iconH / 2;

            bb.Clear(isPending ? TilePermissionColor : TileBaseColor);
            bb.DrawText(line1, 0, top, w, line1H, isPending ? TextDark : TextWhite, 17);
            bb.DrawText(line2, 0, top + line1H, w, line2H, isPending ? TextDark : TextDim, 16);
            drawIcon(bb, cx, cy, isPending ? TextDark : AccentGreen);

            return bb.ToImage();
        }

        // Pulses amber while sessions wait for permission; shows how many and which one comes next
        public static BitmapImage RenderNextWaitingKey(IReadOnlyList<AntigravitySession> waiting, Double breath, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            var large = h >= 100;

            if (waiting.Count == 0)
            {
                bb.Clear(TileBaseColor);
                var emptyTop = CenterTop(h, 20 + 22);
                bb.DrawText("Waiting", 0, emptyTop, w, 20, TextDim, 15);
                bb.DrawText("None", 0, emptyTop + 20, w, 22, TextMuted, 18);
                return bb.ToImage();
            }

            bb.Clear(Blend(new BitmapColor(176, 132, 6), TilePermissionColor, breath));

            const Int32 titleH = 20, countH = 30, nameH = 20;
            var top = CenterTop(h, titleH + countH + nameH);
            bb.DrawText("Waiting", 0, top, w, titleH, TextDark, 15);
            bb.DrawText(waiting.Count.ToString(), 0, top + titleH, w, countH, TextDark, 26);

            var name = waiting[0].DisplayName;
            var max = MaxChars(w, large ? 14 : 12);
            if (name.Length > max)
            {
                name = name.Substring(0, max - 1).TrimEnd() + "…";
            }
            bb.DrawText(name, 2, top + titleH + countH, w - 4, nameH, TextDark, large ? 14 : 12);

            return bb.ToImage();
        }

        public static BitmapImage RenderNewConversationKey(PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            const Int32 plusSize = 22, gap = 10, textH = 20;
            var top = CenterTop(h, plusSize + gap + textH);
            var cx = w / 2;
            var cy = top + plusSize / 2;
            bb.DrawLine(cx - plusSize / 2, cy, cx + plusSize / 2, cy, AccentGreen, 4);
            bb.DrawLine(cx, cy - plusSize / 2, cx, cy + plusSize / 2, AccentGreen, 4);
            bb.DrawText("New Chat", 0, top + plusSize + gap, w, textH, TextWhite, 17);

            return bb.ToImage();
        }

        public static BitmapImage RenderFocusKey(PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            const Int32 lineH = 20, gap = 10, barH = 4;
            var top = CenterTop(h, 2 * lineH + gap + barH);
            bb.DrawText("Antigravity", 0, top, w, lineH, TextWhite, 15);
            bb.DrawText("Open", 0, top + lineH, w, lineH, TextDim, 15);
            bb.FillRectangle((w - 32) / 2, top + 2 * lineH + gap, 32, barH, AccentCyan);

            return bb.ToImage();
        }

        public static BitmapImage RenderRefreshKey(PluginImageSize imageSize, Boolean isSyncing)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            // "Refresh" / "Sync" / status bar, centered as one block
            const Int32 line1H = 20, line2H = 22, gap = 8, barH = 8;
            var top = CenterTop(h, line1H + line2H + gap + barH);
            bb.DrawText("Refresh", 0, top, w, line1H, TextWhite, 15);
            bb.DrawText(isSyncing ? "Sync..." : "Sync", 0, top + line1H, w, line2H, TextWhite, 18);

            var barW = Math.Min(w - 24, 72);
            bb.FillRectangle((w - barW) / 2, top + line1H + line2H + gap, barW, barH, isSyncing ? AccentCyan : AccentGreen);

            return bb.ToImage();
        }

        public static BitmapImage RenderTokenStatsKey(AntigravitySession session, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            if (session == null)
            {
                var emptyTop = CenterTop(h, 18 + 26);
                bb.DrawText("Tokens", 0, emptyTop, w, 18, TextWhite, 16);
                bb.DrawText("-", 0, emptyTop + 18, w, 26, TextDim, 22);
                return bb.ToImage();
            }

            // Header + three rows, centered as one block
            var large = w >= 100;
            var titleH = large ? 18 : 15;
            var rowH = large ? 20 : 16;
            var top = CenterTop(h, titleH + 3 * rowH);
            bb.DrawText("Tokens", 0, top, w, titleH, TextDim, large ? 15 : 12);
            bb.DrawText($"In: {FormatTokens(session.InputTokens)}", 0, top + titleH, w, rowH, TextWhite, large ? 17 : 13);
            bb.DrawText($"Out: {FormatTokens(session.OutputTokens)}", 0, top + titleH + rowH, w, rowH, TextWhite, large ? 17 : 13);
            bb.DrawText($"Cache: {FormatTokens(session.CacheReadTokens)}", 0, top + titleH + 2 * rowH, w, rowH, new BitmapColor(185, 215, 255), large ? 16 : 12);

            return bb.ToImage();
        }

        public static BitmapImage RenderSlotOrderKey(Int32 stateIndex, PluginImageSize imageSize)
        {
            using var bb = new BitmapBuilder(imageSize);
            var w = bb.Width;
            var h = bb.Height;
            bb.Clear(TileBaseColor);

            var strategies = Enum.GetValues<SlotOrderStrategy>();
            var current = strategies[Math.Clamp(stateIndex, 0, strategies.Length - 1)];

            // "Slot Order" / mode name / one marker per mode, centered as one block
            const Int32 titleH = 18, valueH = 24, gap = 8, markH = 6, markW = 14, markGap = 6;
            var top = CenterTop(h, titleH + valueH + gap + markH);
            bb.DrawText("Slot Order", 0, top, w, titleH, TextDim, 14);
            bb.DrawText(current.ToString(), 0, top + titleH, w, valueH, TextWhite, 19);

            var marksW = strategies.Length * markW + (strategies.Length - 1) * markGap;
            var mx = (w - marksW) / 2;
            var my = top + titleH + valueH + gap;
            for (var i = 0; i < strategies.Length; i++)
            {
                bb.FillRectangle(mx + i * (markW + markGap), my, markW, markH, strategies[i] == current ? AccentGreen : TrackColor);
            }

            return bb.ToImage();
        }

        // ---------------------------------------------------------------- Helpers

        private static Int32 CenterTop(Int32 height, Int32 blockHeight) => Math.Max(0, (height - blockHeight) / 2);

        // Key titles like "Claude/GPT 7d" must stay on one line, also on 80 px keys
        private static Int32 TitleFont(String title, Int32 width) =>
            width >= 100 ? (title.Length > 11 ? 13 : 15) : (title.Length > 11 ? 10 : 12);

        private static BitmapColor Blend(BitmapColor from, BitmapColor to, Double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            Byte Mix(Byte a, Byte b) => (Byte)Math.Round(a + (b - a) * t);
            return new BitmapColor(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
        }

        private static String FormatTokens(Int64 tokens)
        {
            var k = tokens / 1000.0;
            if (k >= 1000.0) return $"{k / 1000.0:0.#}M";
            if (k >= 100.0) return $"{k:0}k";
            return $"{k:0.#}k";
        }

        // Check mark spanning 24px wide / 16px high (times scale), centered on (cx, cy)
        private static void DrawCheckMark(BitmapBuilder bb, Int32 cx, Int32 cy, BitmapColor color, Single thickness, Double scale = 1.0)
        {
            Int32 S(Double v) => (Int32)Math.Round(v * scale);
            bb.DrawLine(cx - S(12), cy, cx - S(4), cy + S(8), color, thickness);
            bb.DrawLine(cx - S(4), cy + S(8), cx + S(12), cy - S(8), color, thickness);
        }

        private static void FillRoundedRect(BitmapBuilder bb, Int32 x, Int32 y, Int32 width, Int32 height, Int32 radius, BitmapColor color)
        {
            radius = Math.Min(radius, Math.Min(width, height) / 2);
            bb.FillRectangle(x + radius, y, width - 2 * radius, height, color);
            bb.FillRectangle(x, y + radius, width, height - 2 * radius, color);
            bb.FillCircle(x + radius, y + radius, radius, color);
            bb.FillCircle(x + width - radius, y + radius, radius, color);
            bb.FillCircle(x + radius, y + height - radius, radius, color);
            bb.FillCircle(x + width - radius, y + height - radius, radius, color);
        }

        // Word wrap. Words longer than a line are split after - _ . or before a capital letter,
        // and only cut hard if that is not enough.
        private static List<String> WrapText(String text, Int32 maxCharsPerLine)
        {
            var result = new List<String>();
            if (String.IsNullOrWhiteSpace(text)) return result;

            var currentLine = "";
            void Append(String chunk, Boolean glued)
            {
                var candidate = glued ? currentLine + chunk : (currentLine + " " + chunk).Trim();
                if (candidate.Length <= maxCharsPerLine)
                {
                    currentLine = candidate;
                    return;
                }
                if (currentLine.Length > 0) result.Add(currentLine);
                currentLine = chunk;
            }

            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length <= maxCharsPerLine)
                {
                    Append(word, false);
                    continue;
                }

                var first = true;
                foreach (var piece in SplitLongWord(word, maxCharsPerLine))
                {
                    Append(piece, !first);
                    first = false;
                }
            }

            if (currentLine.Length > 0) result.Add(currentLine);
            return result;
        }

        private static IEnumerable<String> SplitLongWord(String word, Int32 max)
        {
            var start = 0;
            for (var i = 1; i < word.Length; i++)
            {
                var breakAfterSeparator = "-_.".Contains(word[i - 1]);
                var breakBeforeCapital = Char.IsUpper(word[i]) && Char.IsLower(word[i - 1]);
                if (breakAfterSeparator || breakBeforeCapital)
                {
                    foreach (var chunk in HardSplit(word.Substring(start, i - start), max)) yield return chunk;
                    start = i;
                }
            }
            foreach (var chunk in HardSplit(word.Substring(start), max)) yield return chunk;
        }

        private static IEnumerable<String> HardSplit(String s, Int32 max)
        {
            for (var i = 0; i < s.Length; i += max)
            {
                yield return s.Substring(i, Math.Min(max, s.Length - i));
            }
        }
    }
}
