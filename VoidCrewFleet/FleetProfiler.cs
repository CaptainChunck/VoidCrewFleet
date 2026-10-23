using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Developer profiler (Diagnostics > Profile, off by default). It times every Fleet patch method and periodic handler,
    /// and every 10 s logs where the plugin's own time went: milliseconds per second, calls per second, the slowest call and
    /// the bytes allocated, with the frame rate and garbage collections for context. A patch method is timed by a Harmony
    /// prefix and finalizer of its own, so its figures include a little of that wrapper: they are an upper bound. A call made
    /// inside another timed call counts in both.
    /// </summary>
    internal static class FleetProfiler
    {
        private const float WindowSeconds = 10f;

        private sealed class Stat
        {
            public string Name;
            public bool Patch;
            public long Ticks;
            public long MaxTicks;
            public long Bytes;
            public int Calls;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static readonly Func<long> AllocatedBytes = BindAllocatedBytes();

        [ThreadStatic] private static Stack<long> _starts;
        [ThreadStatic] private static Stack<long> _startBytes;

        private static bool _on;
        private static float _windowStart;
        private static int _frames;
        private static float _worstFrame;
        private static int _collections;

        public static bool On
        {
            get { return _on; }
        }

        /// <summary>Once the game has loaded and every patch is in: wraps each patch method in a timer, when switched on.</summary>
        public static void Install(Harmony harmony)
        {
            if (_on || harmony == null || Settings.Profile == null || !Settings.Profile.Value) return;
            var begin = new HarmonyMethod(typeof(FleetProfiler).GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic));
            var end = new HarmonyMethod(typeof(FleetProfiler).GetMethod("End", BindingFlags.Static | BindingFlags.NonPublic));
            int wrapped = 0;
            foreach (MethodInfo hook in Patcher.AppliedHooks())
            {
                try
                {
                    harmony.Patch(hook, prefix: begin, finalizer: end);
                    wrapped++;
                }
                catch (Exception e)
                {
                    Log.Warn("Profiler: can't time " + hook.DeclaringType.Name + "." + hook.Name + ": " + e.Message);
                }
            }
            Calibrate(harmony, begin, end);
            _on = true;
            StartWindow();
            Log.Info("Profiler on: timing " + wrapped + " patch methods and every periodic handler; the timer's own cost of " +
                     (_overheadTicks * 1000000.0 / Stopwatch.Frequency).ToString("0.000") + " µs per patch call is taken off; allocation counts " +
                     (AllocatedBytes != null ? "on" : "unavailable") + ".");
        }

        /// <summary>What the timer itself adds to each timed patch call, measured on an empty method: taken off in the reports.</summary>
        private static long _overheadTicks;

        private static void Calibrate(Harmony harmony, HarmonyMethod begin, HarmonyMethod end)
        {
            try
            {
                MethodInfo probe = typeof(FleetProfiler).GetMethod("Probe", BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(probe, prefix: begin, finalizer: end);
                for (int i = 0; i < 2000; i++) Probe();
                Stat stat;
                lock (Gate)
                {
                    Stats.TryGetValue(probe, out stat);
                    if (stat != null)
                    {
                        stat.Ticks = 0;
                        stat.Calls = 0;
                        stat.MaxTicks = 0;
                    }
                }
                for (int i = 0; i < 20000; i++) Probe();
                lock (Gate)
                {
                    if (Stats.TryGetValue(probe, out stat) && stat.Calls > 0) _overheadTicks = stat.Ticks / stat.Calls;
                    Stats.Remove(probe);
                }
                harmony.Unpatch(probe, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception e)
            {
                Log.Warn("Profiler: couldn't measure the timer's own cost: " + e.Message);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Probe()
        {
        }

        private static void Begin()
        {
            if (_starts == null)
            {
                _starts = new Stack<long>();
                _startBytes = new Stack<long>();
            }
            _startBytes.Push(AllocatedBytes != null ? AllocatedBytes() : 0L);
            _starts.Push(Stopwatch.GetTimestamp());
        }

        private static Exception End(MethodBase __originalMethod, Exception __exception)
        {
            long now = Stopwatch.GetTimestamp();
            long bytes = AllocatedBytes != null ? AllocatedBytes() : 0L;
            if (_starts != null && _starts.Count > 0) Add(__originalMethod, true, now - _starts.Pop(), bytes - _startBytes.Pop());
            return __exception;
        }

        /// <summary>Runs one periodic handler (FleetRuntime), timed.</summary>
        public static void Measure(Action handler)
        {
            long bytes = AllocatedBytes != null ? AllocatedBytes() : 0L;
            long start = Stopwatch.GetTimestamp();
            try
            {
                handler();
            }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                Add(handler.Method, false, ticks, (AllocatedBytes != null ? AllocatedBytes() : 0L) - bytes);
            }
        }

        private static void Add(MethodBase method, bool patch, long ticks, long bytes)
        {
            lock (Gate)
            {
                Stat stat;
                if (!Stats.TryGetValue(method, out stat))
                {
                    Stats[method] = stat = new Stat { Name = method.DeclaringType.Name + "." + method.Name, Patch = patch };
                }
                stat.Ticks += ticks;
                stat.Calls++;
                if (ticks > stat.MaxTicks) stat.MaxTicks = ticks;
                if (bytes > 0) stat.Bytes += bytes;
            }
        }

        /// <summary>Every frame (FleetRuntime): counts frames, and logs a report every 10 s.</summary>
        public static void Frame()
        {
            if (!_on) return;
            _frames++;
            if (Time.unscaledDeltaTime > _worstFrame) _worstFrame = Time.unscaledDeltaTime;
            float elapsed = Time.unscaledTime - _windowStart;
            if (elapsed < WindowSeconds) return;
            try
            {
                Report(elapsed);
            }
            catch (Exception e)
            {
                Log.Failure("Profiler report", e);
            }
            StartWindow();
        }

        private static void StartWindow()
        {
            _windowStart = Time.unscaledTime;
            _frames = 0;
            _worstFrame = 0f;
            _collections = GC.CollectionCount(0);
        }

        private static void Report(float seconds)
        {
            var stats = new List<Stat>();
            lock (Gate)
            {
                foreach (Stat stat in Stats.Values)
                {
                    if (stat.Calls == 0) continue;
                    // A patch call's figures lose the timer's own cost (measured at start-up).
                    long ticks = stat.Patch ? Math.Max(0L, stat.Ticks - stat.Calls * _overheadTicks) : stat.Ticks;
                    long max = stat.Patch ? Math.Max(0L, stat.MaxTicks - _overheadTicks) : stat.MaxTicks;
                    stats.Add(new Stat { Name = stat.Name, Patch = stat.Patch, Ticks = ticks, MaxTicks = max, Bytes = stat.Bytes, Calls = stat.Calls });
                    stat.Ticks = 0;
                    stat.MaxTicks = 0;
                    stat.Bytes = 0;
                    stat.Calls = 0;
                }
            }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            double fps = _frames / seconds;
            // The self-test's own work (reports, screenshots) is shown but not counted: players never run it.
            double total = stats.Where(s => !s.Name.StartsWith("AutoTest.", StringComparison.Ordinal)).Sum(s => s.Ticks) * msPerTick / seconds;
            double kilobytes = stats.Sum(s => s.Bytes) / 1024.0 / seconds;
            var text = new StringBuilder();
            text.AppendLine("Profiler, last " + seconds.ToString("0") + " s: " + fps.ToString("0") + " fps (worst frame " + (_worstFrame * 1000f).ToString("0") +
                            " ms), " + (GC.CollectionCount(0) - _collections) + " garbage collections. Fleet code: " + total.ToString("0.00") + " ms per second, " +
                            (fps > 0 ? (total / fps).ToString("0.000") : "?") + " ms per frame, " + kilobytes.ToString("0.0") + " KB allocated per second. " +
                            (GameSessionManager.InHub ? "Hub" : "Mission") + ", " + FleetShips.All.Count + " ships, " + PhotonNetwork.PlayerList.Length + " players.");
            foreach (Stat stat in stats.OrderByDescending(s => s.Ticks).Take(25))
            {
                text.AppendLine("  " + (stat.Ticks * msPerTick / seconds).ToString("0.000") + " ms/s  " + (stat.Calls / seconds).ToString("0") + " calls/s  max " +
                                (stat.MaxTicks * msPerTick).ToString("0.00") + " ms  " + (stat.Bytes / 1024.0 / seconds).ToString("0.0") + " KB/s  " + stat.Name +
                                (stat.Name.StartsWith("AutoTest.", StringComparison.Ordinal) ? " (self-test, not counted)" : ""));
            }
            Log.Info(text.ToString());
        }

        private static Func<long> BindAllocatedBytes()
        {
            try
            {
                MethodInfo method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
                return method != null ? (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
