using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace Automatics.AutomaticMapping
{
    internal static class MappingProfiler
    {
        public const int SlotDynamicMapping = 0;
        public const int SlotStaticMapping = 1;
        public const int SlotCacheStaticObjects = 2;
        public const int SlotAnimatePins = 3;
        public const int SlotGetClosestPin = 4;
        public const int SlotRefreshPins = 5;
        private const int SlotCount = 6;

        private const double FlushIntervalSeconds = 1.0;

        private static readonly string[] SlotNames =
        {
            "DynamicMapping",
            "StaticMapping",
            "CacheStaticObjects",
            "AnimatePins",
            "GetClosestPin",
            "RefreshPins"
        };

        private static readonly long[] ElapsedTicks = new long[SlotCount];
        private static readonly long[] CallCount = new long[SlotCount];
        private static long _flushAnchor;

        public static bool IsEnabled => Config.MappingPerformanceLog;

        public static Scope BeginScope(int slot)
        {
            return new Scope(slot, IsEnabled ? Stopwatch.GetTimestamp() : 0L);
        }

        public static void FlushIfDue()
        {
            if (!IsEnabled) return;

            var now = Stopwatch.GetTimestamp();
            if (_flushAnchor == 0L)
            {
                _flushAnchor = now;
                return;
            }

            var elapsedSeconds = (double)(now - _flushAnchor) / Stopwatch.Frequency;
            if (elapsedSeconds < FlushIntervalSeconds) return;
            _flushAnchor = now;

            var sb = new StringBuilder("[MappingProfiler] ");
            var anyEntries = false;
            for (var i = 0; i < SlotCount; i++)
            {
                var ticks = Interlocked.Exchange(ref ElapsedTicks[i], 0L);
                var count = Interlocked.Exchange(ref CallCount[i], 0L);
                if (count == 0) continue;

                if (anyEntries) sb.Append(' ');
                anyEntries = true;

                var totalMs = ticks * 1000.0 / Stopwatch.Frequency;
                var avgUs = ticks * 1_000_000.0 / Stopwatch.Frequency / count;
                sb.Append(SlotNames[i])
                    .Append('=')
                    .Append(totalMs.ToString("F2"))
                    .Append("ms/")
                    .Append(count)
                    .Append("x(avg ")
                    .Append(avgUs.ToString("F1"))
                    .Append("us)");
            }

            // Write to the BepInEx log source directly so mapping_performance_log
            // always produces output, bypassing the mod-level enable_logging /
            // log_level_to_allow_logging gate. BepInEx's own LogLevels filter
            // still applies, but Info is in its default set.
            if (anyEntries)
                Automatics.LogSource.Log(LogLevel.Info, sb.ToString());
        }

        public static void Reset()
        {
            for (var i = 0; i < SlotCount; i++)
            {
                Interlocked.Exchange(ref ElapsedTicks[i], 0L);
                Interlocked.Exchange(ref CallCount[i], 0L);
            }

            _flushAnchor = 0L;
        }

        private static void Accumulate(int slot, long elapsed)
        {
            Interlocked.Add(ref ElapsedTicks[slot], elapsed);
            Interlocked.Increment(ref CallCount[slot]);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly int _slot;
            private readonly long _start;

            internal Scope(int slot, long start)
            {
                _slot = slot;
                _start = start;
            }

            public void Dispose()
            {
                if (_start == 0L) return;
                Accumulate(_slot, Stopwatch.GetTimestamp() - _start);
            }
        }
    }
}
