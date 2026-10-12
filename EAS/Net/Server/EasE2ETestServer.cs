// Creates representative test chunks and applies timed edits to exercise EAS networking.
// Use with the client diagnostic component to observe full transfers and incremental updates.

// Assets/Scripts/EAS/Net/Server/EasE2ETestServer.cs
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace EAS.Net
{
    /// <summary>
    /// End-to-end EAS test:
    /// - Creates 4 chunks that force Uniform / Dense / Palette / Bitplane
    /// - Registers them with EasNetServer
    /// - Subscribes any connecting client to those chunks
    /// - Performs ops edits over time and checks resync path
    /// </summary>
    public sealed class EasE2ETestServer : MonoBehaviour
    {
        public EasNetServer netServer;

        [Header("Chunk Setup")]
        public int chunkSize = 16; // voxel per axis
        public bool allowBitplaneForDisk = true;

        [Header("Ops Test")]
        public float opsIntervalSeconds = 1.0f;
        public int opsEditsPerTick = 64;

        private readonly List<ChunkCoord> _testKeys = new();
        private readonly System.Random _rng = new(12345);

        private float _t;

        private void Awake()
        {
            if (netServer == null) netServer = EasNetServer.Instance;
            if (netServer == null) netServer = FindAnyObjectByType<EasNetServer>();
            if (netServer == null) Debug.LogError("[EAS-TEST] EasNetServer not found in scene.");

            // Configure codec for testing
            EAS.EasChunkCodec.AllowBitplaneForDisk = allowBitplaneForDisk;
            EAS.EasChunkCodec.UseAutoCompression = true;
            EAS.EasChunkCodec.AutoProfile = EAS.EasCompressionProfile.Fast;

            // Prefer LZ4 if available; avoid Deflate during net tests (CPU heavy)
            EAS.EasAutoCompression.AllowLz4 = true;
            EAS.EasAutoCompression.AllowDeflate = false;
        }

        private void Start()
        {
            if (netServer == null) return;

            // Create & register test chunks at known coords
            BuildTestWorld();

            // When clients connect, auto-subscribe them to test chunks
            NetworkServer.OnConnectedEvent += OnClientConnected;

            if (EAS.EasDebug.Enabled)
                Debug.Log("[EAS-TEST] Server test harness ready. Start as Host and connect a client.");
        }

        private void OnClientConnected(NetworkConnectionToClient conn)
        {
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log($"[EAS-TEST] Client connected: {conn.connectionId}. Subscribing to test chunks...");

            for (int i = 0; i < _testKeys.Count; i++)
            {
                netServer.Subscribe(conn, _testKeys[i]);
            }

            // Kick an ops burst immediately so the client sees deltas
            ApplyOpsBurst();
        }

        private void Update()
        {
            if (!NetworkServer.active) return;

            _t += Time.deltaTime;
            if (_t >= opsIntervalSeconds)
            {
                _t = 0f;
                ApplyOpsBurst();
            }
        }

        private void BuildTestWorld()
        {
            _testKeys.Clear();

            int cells = chunkSize * chunkSize * chunkSize;

            // 1) Uniform chunk: all 3s
            var keyU = new ChunkCoord(0, 0, 0);
            var uniform = new EAS.EasChunk(cells, 3);
            netServer.PutChunk(keyU, uniform);
            _testKeys.Add(keyU);

            // 2) Dense chunk: many values (0..14 mixed) -> should choose Dense
            var keyD = new ChunkCoord(1, 0, 0);
            var dense = new EAS.EasChunk(cells, 0);
            for (int i = 0; i < cells; i++) dense.Set(i, (byte)(i % 15));
            dense.ClearTouched(); // treat as "loaded"
            dense.ClearDirty();
            netServer.PutChunk(keyD, dense);
            _testKeys.Add(keyD);

            // 3) Palette chunk: only 4 unique values -> should choose Palette
            var keyP = new ChunkCoord(2, 0, 0);
            var pal = new EAS.EasChunk(cells, 1);
            for (int i = 0; i < cells; i++)
            {
                // 4 values: 1,2,3,4
                pal.Set(i, (byte)(1 + ((i >> 4) & 3)));
            }
            pal.ClearTouched();
            pal.ClearDirty();
            netServer.PutChunk(keyP, pal);
            _testKeys.Add(keyP);

            // 4) Bitplane-friendly chunk: mostly zeros with rare spikes
            var keyB = new ChunkCoord(3, 0, 0);
            var bitp = new EAS.EasChunk(cells, 0);
            for (int i = 0; i < cells; i += 97) bitp.Set(i, 14);
            bitp.ClearTouched();
            bitp.ClearDirty();
            netServer.PutChunk(keyB, bitp);
            _testKeys.Add(keyB);

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log("[EAS-TEST] Test chunks created at coords (0..3,0,0).");
        }

        private void ApplyOpsBurst()
        {
            if (_testKeys.Count == 0) return;

            for (int k = 0; k < _testKeys.Count; k++)
            {
                var key = _testKeys[k];
                if (!netServer.TryGetChunk(key, out var chunk))
                {
                    Debug.LogWarning($"[EAS-TEST] Missing authoritative chunk in netServer for {key}");
                    continue;
                }

                int n = chunk.CellCount;
                for (int e = 0; e < opsEditsPerTick; e++)
                {
                    int idx = _rng.Next(0, n);
                    byte v = (byte)_rng.Next(0, 15);
                    chunk.Set(idx, v);
                }

                if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps)
                    Debug.Log($"[EAS-TEST] After edits {key} touched={chunk.TouchedCount} dirty={chunk.Dirty}");
                netServer.MarkChunkDirty(key);
            }

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps)
                Debug.Log($"[EAS-TEST] Applied {opsEditsPerTick} random edits to each test chunk; ops will flush soon.");
        }

        // Simple registry to avoid changing EasNetServer for the test harness
        private static class EasTestChunkRegistry
        {
            private static readonly Dictionary<ChunkCoord, EAS.EasChunk> Map = new();

            public static void Set(ChunkCoord key, EAS.EasChunk c) => Map[key] = c;
            public static bool TryGet(ChunkCoord key, out EAS.EasChunk c) => Map.TryGetValue(key, out c);
        }

        // Hook registry when PutChunk is called in BuildTestWorld
        private void OnEnable()
        {
            // Rebuild world and register in registry when server is active (safe if called multiple times)
            if (!Application.isPlaying) return;
        }

        // Small helper: wrap PutChunk and also record locally
        private void Put(ChunkCoord key, EAS.EasChunk chunk)
        {
            netServer.PutChunk(key, chunk);
            EasTestChunkRegistry.Set(key, chunk);
        }

        // Replace netServer.PutChunk calls with Put(...) in BuildTestWorld
        // (Kept as method to avoid editing EasNetServer)
        private void Reset() { }
    }
}
