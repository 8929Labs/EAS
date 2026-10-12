// Updates server chunk subscriptions when a player crosses a chunk boundary.
// Desired coordinates form either a cube or an L1 diamond around the player chunk.

// Assets/Scripts/EAS/Net/Server/EasServerChunkInterest.cs
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace EAS.Net
{
    /// <summary>
    /// Server-side interest manager for EAS chunks.
    /// Attach to the player prefab (must have NetworkIdentity).
    /// Requires an EasNetServer present in the scene on the server.
    /// </summary>
    public sealed class EasServerChunkInterest : NetworkBehaviour
    {
        [Header("Chunk Settings")]
        [Tooltip("Chunk size in world units. Example: 16 if each voxel is 1 unit and chunk is 16^3.")]
        public int chunkSize = 16;

        [Tooltip("Render distance in chunks (Manhattan cube / Chebyshev cube depending on include corners).")]
        public int renderDistanceChunks = 6;

        [Tooltip("If true, includes corner chunks (cube). If false, uses a cheaper diamond-ish shape.")]
        public bool includeCorners = true;

        [Header("Update Rate")]
        [Tooltip("How often (seconds) to recompute subscriptions on server.")]
        public float updateInterval = 0.25f;

        private EasNetServer _server;
        private NetworkConnectionToClient _conn;

        private ChunkCoord _lastCenter;
        private bool _hasCenter;

        // track current subs for this player (server-side only)
        private readonly HashSet<ChunkCoord> _current = new();

        private double _nextUpdateTime;

        public override void OnStartServer()
        {
            _server = FindAnyObjectByType<EasNetServer>();
            if (_server == null)
            {
                Debug.LogError("[EAS] EasNetServer not found in scene. Chunk interest cannot run.");
                enabled = false;
                return;
            }

            _conn = connectionToClient;
            _nextUpdateTime = NetworkTime.time;
        }

        [ServerCallback]
        private void Update()
        {
            if (_server == null) return;
            if (NetworkTime.time < _nextUpdateTime) return;

            _nextUpdateTime = NetworkTime.time + updateInterval;

            var center = WorldToChunkCoord(transform.position);

            if (!_hasCenter || !center.Equals(_lastCenter))
            {
                _hasCenter = true;
                _lastCenter = center;
                RebuildSubscriptions(center);
            }
        }

        [Server]
        private ChunkCoord WorldToChunkCoord(Vector3 pos)
        {
            int cx = Mathf.FloorToInt(pos.x / chunkSize);
            int cy = Mathf.FloorToInt(pos.y / chunkSize);
            int cz = Mathf.FloorToInt(pos.z / chunkSize);
            return new ChunkCoord(cx, cy, cz);
        }

        [Server]
        private void RebuildSubscriptions(ChunkCoord center)
        {
            // Desired set
            var desired = ListPool<ChunkCoord>.Get();
            BuildDesired(center, desired);

            // Unsubscribe removed
            // (iterate a snapshot to avoid modifying while enumerating)
            var toRemove = ListPool<ChunkCoord>.Get();
            foreach (var key in _current)
            {
                if (!Contains(desired, key)) toRemove.Add(key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                var key = toRemove[i];
                _current.Remove(key);
                _server.Unsubscribe(_conn, key);
            }

            // Subscribe new
            for (int i = 0; i < desired.Count; i++)
            {
                var key = desired[i];
                if (_current.Add(key))
                {
                    _server.Subscribe(_conn, key);
                }
            }

            ListPool<ChunkCoord>.Release(desired);
            ListPool<ChunkCoord>.Release(toRemove);
        }

        [Server]
        private void BuildDesired(ChunkCoord center, List<ChunkCoord> outKeys)
        {
            int r = Mathf.Max(0, renderDistanceChunks);

            if (includeCorners)
            {
                // Cube: (2r+1)^3
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            outKeys.Add(new ChunkCoord(center.x + dx, center.y + dy, center.z + dz));
                        }
                    }
                }
            }
            else
            {
                // Cheaper: L1 ball-ish (diamond). Greatly reduces number of chunks.
                for (int dz = -r; dz <= r; dz++)
                {
                    int rz = r - Mathf.Abs(dz);
                    for (int dy = -rz; dy <= rz; dy++)
                    {
                        int ry = rz - Mathf.Abs(dy);
                        for (int dx = -ry; dx <= ry; dx++)
                        {
                            outKeys.Add(new ChunkCoord(center.x + dx, center.y + dy, center.z + dz));
                        }
                    }
                }
            }
        }

        [Server]
        private static bool Contains(List<ChunkCoord> list, ChunkCoord key)
        {
            // For small lists, linear scan is fine. If your render distance is big, we can switch to a HashSet.
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Equals(key)) return true;
            }
            return false;
        }

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
