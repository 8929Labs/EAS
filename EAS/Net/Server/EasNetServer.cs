// Owns authoritative chunks, per-connection subscriptions, and baseline tracking.
// A Hot/Warm/Cold scheduler batches touched-cell operations and sends full data when needed.

// Assets/Scripts/EAS/Net/Server/EasNetServer.cs
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace EAS.Net
{
    /// <summary>
    /// Attach this to a server-side object (can be the same object as your network manager bootstrap).
    /// You call Subscribe/Unsubscribe based on render distance and player movement.
    /// You call MarkChunkDirty(key) when you edit a chunk, and it will flush ops using a Hot/Warm/Cold scheduler.
    /// </summary>
    public sealed class EasNetServer : NetworkBehaviour
    {
        // Authoritative world chunks (you will likely replace this with your own chunk storage)
        private readonly Dictionary<ChunkCoord, EAS.EasChunk> _world = new();

        // Current authoritative baseline per chunk
        private readonly Dictionary<ChunkCoord, uint> _chunkBaseline = new();

        // For each connection: subscribed chunks and baseline it currently has
        private readonly Dictionary<NetworkConnectionToClient, HashSet<ChunkCoord>> _subs = new();
        private readonly Dictionary<NetworkConnectionToClient, Dictionary<ChunkCoord, uint>> _connBaseline = new();

        // Active chunks which may flush ops (Hot or Warm). Cold chunks are not in this set.
        private readonly HashSet<ChunkCoord> _activeDirty = new();

        // temp list reused to avoid allocations when building ops
        private readonly List<int> _tempTouched = new(256);

        [Header("Hot/Warm/Cold Scheduling")]

        [Tooltip("Hot flush rate (Hz). Hot chunks are currently being edited.")]
        public float hotSendRateHz = 15f;

        [Tooltip("Warm flush rate (Hz). Warm chunks haven't been edited recently.")]
        public float warmSendRateHz = 3f;

        [Tooltip("Seconds since last edit to switch Hot -> Warm.")]
        public float hotToWarmSeconds = 1.0f;

        [Tooltip("Seconds since last edit to switch Warm -> Cold (stop flushing entirely).")]
        public float warmToColdSeconds = 8.0f;

        [Header("Batching / Debounce")]

        [Tooltip("Minimum delay after an edit before flushing ops (seconds). Helps coalesce rapid edits.")]
        public float minCoalesceDelay = 0.05f;

        [Tooltip("Maximum time to keep delaying a flush while edits keep coming (seconds).")]
        public float maxCoalesceDelay = 0.20f;

        // Per-chunk replication scheduling state
        private enum Heat : byte { Hot = 0, Warm = 1, Cold = 2 }

        private struct ChunkSched
        {
            public Heat heat;

            public double lastEditTime;    // last time MarkChunkDirty happened
            public double batchStartTime;  // start of current "edit burst" batch
            public double nextFlushTime;   // when server should attempt to flush ops
        }

        private readonly Dictionary<ChunkCoord, ChunkSched> _sched = new();

        public static EasNetServer Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[EAS] Multiple EasNetServer instances found. Destroying duplicate: " + name);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log("[EAS] EasNetServer OnStartServer");

            // TEST: create a single chunk at (0,0,0)
            var key = new ChunkCoord(0, 0, 0);
            var chunk = new EAS.EasChunk(16 * 16 * 16, 1); // all 1s
            PutChunk(key, chunk);

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log("[EAS] Put test chunk at (0,0,0)");
        }

        private void Update()
        {
            if (!isServer) return;

            // Flush any chunks whose nextFlushTime has arrived.
            FlushDueChunks(NetworkTime.time);
        }

        // -----------------------
        // Public API you call
        // -----------------------

        public void PutChunk(ChunkCoord key, EAS.EasChunk chunk)
        {
            _world[key] = chunk;
            if (!_chunkBaseline.ContainsKey(key)) _chunkBaseline[key] = 0;

            // Ensure sched state exists (defaults to Cold until edited)
            if (!_sched.ContainsKey(key))
            {
                _sched[key] = new ChunkSched
                {
                    heat = Heat.Cold,
                    lastEditTime = 0,
                    batchStartTime = 0,
                    nextFlushTime = double.PositiveInfinity
                };
            }
        }

        /// <summary>
        /// Call this after you edit a chunk.
        /// This promotes the chunk to HOT, schedules an ops flush with coalescing,
        /// and adds it to the active set.
        /// </summary>
        public void MarkChunkDirty(ChunkCoord key)
        {
            if (!_world.ContainsKey(key)) return;

            double now = NetworkTime.time;

            if (!_sched.TryGetValue(key, out var s))
            {
                s = new ChunkSched
                {
                    heat = Heat.Cold,
                    lastEditTime = now,
                    batchStartTime = now,
                    nextFlushTime = now + minCoalesceDelay
                };
            }

            // If this is a new active burst (was cold or not currently active), reset batch start.
            if (s.heat == Heat.Cold || !_activeDirty.Contains(key) || double.IsInfinity(s.nextFlushTime))
            {
                s.batchStartTime = now;
                s.nextFlushTime = now + minCoalesceDelay;
            }
            else
            {
                // Debounce: push next flush forward, but cap at batchStartTime + maxCoalesceDelay
                double desired = now + minCoalesceDelay;
                double cap = s.batchStartTime + maxCoalesceDelay;

                // we want "at least desired", but never beyond cap
                s.nextFlushTime = Math.Min(cap, desired);
            }

            s.lastEditTime = now;
            s.heat = Heat.Hot;

            _sched[key] = s;
            _activeDirty.Add(key);

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps)
                Debug.Log($"[EAS] MarkChunkDirty {key} heat={s.heat} nextFlush={s.nextFlushTime:F3} (active={_activeDirty.Count})");
        }

        public void Subscribe(NetworkConnectionToClient conn, ChunkCoord key)
        {
            if (!_world.TryGetValue(key, out var chunk)) return;

            if (!_subs.TryGetValue(conn, out var set)) _subs[conn] = set = new HashSet<ChunkCoord>();
            if (!_connBaseline.TryGetValue(conn, out var baselines)) _connBaseline[conn] = baselines = new Dictionary<ChunkCoord, uint>();

            if (!set.Add(key)) return;

            uint baseline = _chunkBaseline.TryGetValue(key, out var b) ? b : 0;
            baselines[key] = baseline;

            // Send full baseline chunk
            byte[] blob = EAS.EasChunkCodec.EncodeForDisk(chunk);
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
            {
                Debug.Log($"[EAS] Subscribe conn={conn.connectionId} chunk={key}");
                UnityEngine.Debug.Log($"[EAS] FULL(subscribe) {key} -> {EAS.EasHeaderPeek.PeekToString(blob)}");
            }
            conn.Send(new ChunkFullMsg { cx = key.x, cy = key.y, cz = key.z, baselineId = baseline, blob = blob });
        }

        public void Unsubscribe(NetworkConnectionToClient conn, ChunkCoord key)
        {
            if (_subs.TryGetValue(conn, out var set)) set.Remove(key);
            if (_connBaseline.TryGetValue(conn, out var b)) b.Remove(key);
            conn.Send(new ChunkUnsubscribeMsg { cx = key.x, cy = key.y, cz = key.z });
        }

        public bool TryGetChunk(ChunkCoord key, out EAS.EasChunk chunk) => _world.TryGetValue(key, out chunk);

        public bool TryGetChunk(int cx, int cy, int cz, out EAS.EasChunk chunk) =>
          _world.TryGetValue(new ChunkCoord(cx, cy, cz), out chunk);

        // -----------------------
        // Sending updates (Hot/Warm/Cold)
        // -----------------------

        private void FlushDueChunks(double now)
        {
            if (_activeDirty.Count == 0) return;

            // Snapshot active keys to avoid modifying collection while iterating
            var keys = ListPool<ChunkCoord>.Get();
            keys.AddRange(_activeDirty);

            int flushed = 0;

            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];

                if (!_sched.TryGetValue(key, out var s))
                    continue;

                // Skip if not yet due
                if (now < s.nextFlushTime)
                    continue;

                // Heat transitions based on time since last edit
                double idle = now - s.lastEditTime;
                if (idle >= warmToColdSeconds) s.heat = Heat.Cold;
                else if (idle >= hotToWarmSeconds) s.heat = Heat.Warm;
                else s.heat = Heat.Hot;

                // If cold, stop flushing and remove from active set.
                if (s.heat == Heat.Cold)
                {
                    s.nextFlushTime = double.PositiveInfinity;
                    _sched[key] = s;
                    _activeDirty.Remove(key);
                    continue;
                }

                if (!_world.TryGetValue(key, out var chunk))
                {
                    // Chunk missing; stop tracking
                    s.nextFlushTime = double.PositiveInfinity;
                    _sched[key] = s;
                    _activeDirty.Remove(key);
                    continue;
                }

                // Build ops from touched. IMPORTANT: this clears touched on the authoritative chunk.
                byte[] ops = EasChunkOpsCodec.EncodeFromTouched(chunk, _tempTouched, clearTouched: true);

                // If no ops, we still may want to cool down the chunk.
                if (ops.Length > 5)
                {
                    uint oldBaseline = _chunkBaseline.TryGetValue(key, out var b) ? b : 0;
                    uint newBaseline = oldBaseline + 1;
                    _chunkBaseline[key] = newBaseline;

                    // Broadcast ops to all subscribers
                    foreach (var kvp in _subs)
                    {
                        var conn = kvp.Key;
                        var set = kvp.Value;
                        if (!set.Contains(key)) continue;

                        if (!_connBaseline.TryGetValue(conn, out var baselines)) continue;
                        if (!baselines.TryGetValue(key, out var clientBaseline))
                        {
                            SendFull(conn, key, newBaseline);
                            baselines[key] = newBaseline;
                            continue;
                        }

                        if (clientBaseline != oldBaseline)
                        {
                            SendFull(conn, key, newBaseline);
                            baselines[key] = newBaseline;
                            continue;
                        }

                        conn.Send(new ChunkOpsMsg
                        {
                            cx = key.x,
                            cy = key.y,
                            cz = key.z,
                            baselineId = oldBaseline,
                            newBaselineId = newBaseline,
                            ops = ops
                        });

                        baselines[key] = newBaseline;
                    }

                    chunk.ClearDirty();
                    flushed++;
                }

                // Schedule next flush based on heat
                double hz = (s.heat == Heat.Hot) ? Mathf.Max(1f, hotSendRateHz) : Mathf.Max(0.1f, warmSendRateHz);
                s.nextFlushTime = now + (1.0 / hz);

                // Keep batchStartTime stable until we go cold; next edit will reset it anyway.
                _sched[key] = s;
            }

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps && flushed > 0)
                Debug.Log($"[EAS] FlushDueChunks flushed={flushed} active={_activeDirty.Count}");

            ListPool<ChunkCoord>.Release(keys);
        }

        private void SendFull(NetworkConnectionToClient conn, ChunkCoord key, uint baselineToSend)
        {
            if (!_world.TryGetValue(key, out var chunk)) return;
            byte[] blob = EAS.EasChunkCodec.EncodeForDisk(chunk);
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogCodecHeaders)
                UnityEngine.Debug.Log($"[EAS] FULL {key} -> {EAS.EasHeaderPeek.PeekToString(blob)}");
            conn.Send(new ChunkFullMsg { cx = key.x, cy = key.y, cz = key.z, baselineId = baselineToSend, blob = blob });
        }

        // -----------------------
        // Resync handling (optional)
        // NOTE: You still need to RegisterHandler for ChunkResyncRequestMsg somewhere on server.
        // -----------------------
        private void OnResyncRequest(NetworkConnectionToClient conn, ChunkResyncRequestMsg msg)
        {
            var key = new ChunkCoord(msg.cx, msg.cy, msg.cz);

            if (!_subs.TryGetValue(conn, out var set) || !set.Contains(key)) return;
            if (!_chunkBaseline.TryGetValue(key, out var baseline)) baseline = 0;

            SendFull(conn, key, baseline);

            if (!_connBaseline.TryGetValue(conn, out var baselines)) _connBaseline[conn] = baselines = new Dictionary<ChunkCoord, uint>();
            baselines[key] = baseline;
        }

        // -----------------------
        // Tiny list pool helper (avoids GC for snapshots)
        // -----------------------
        private static class ListPool<T>
        {
            private static readonly Stack<List<T>> Pool = new();

            public static List<T> Get() => Pool.Count > 0 ? Pool.Pop() : new List<T>(256);

            public static void Release(List<T> list)
            {
                list.Clear();
                Pool.Push(list);
            }
        }
    }
}