// Assets/Scripts/EAS/Net/Client/EasNetClient.cs
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace EAS.Net
{
    public sealed class EasNetClient : MonoBehaviour
    {
        // public hook if you want to rebuild meshes
        public System.Action<ChunkCoord> OnChunkChanged;

        private readonly Dictionary<ChunkCoord, EAS.EasChunk> _chunks = new();
        private readonly Dictionary<ChunkCoord, uint> _baseline = new();

        private void Awake()
        {
            NetworkClient.RegisterHandler<ChunkFullMsg>(OnChunkFull, false);
            NetworkClient.RegisterHandler<ChunkOpsMsg>(OnChunkOps, false);
            NetworkClient.RegisterHandler<ChunkUnsubscribeMsg>(OnChunkUnsub, false);
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log("[EAS] EasNetClient Awake - registering handlers");
        }

        public bool TryGetChunk(int cx, int cy, int cz, out EAS.EasChunk chunk) =>
          _chunks.TryGetValue(new ChunkCoord(cx, cy, cz), out chunk);

        private void OnChunkFull(ChunkFullMsg msg)
        {
            var key = new ChunkCoord(msg.cx, msg.cy, msg.cz);
            var chunk = EAS.EasChunkCodec.DecodeFromDisk(msg.blob);
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogCodecHeaders)
                UnityEngine.Debug.Log($"[EAS] Client FULL {key} -> {EAS.EasHeaderPeek.PeekToString(msg.blob)}");

            _chunks[key] = chunk;
            _baseline[key] = msg.baselineId;

            OnChunkChanged?.Invoke(key);
        }

        private void OnChunkOps(ChunkOpsMsg msg)
        {
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps)
                Debug.Log($"[EAS] Client received OPS {msg.cx},{msg.cy},{msg.cz} baseline={msg.baselineId}->{msg.newBaselineId} bytes={msg.ops?.Length}");
            var key = new ChunkCoord(msg.cx, msg.cy, msg.cz);

            if (!_chunks.TryGetValue(key, out var chunk) || !_baseline.TryGetValue(key, out var have))
            {
                // We don't have the chunk yet; ask for resync.
                RequestResync(key, 0);
                return;
            }

            if (have != msg.baselineId)
            {
                Debug.LogWarning($"[EAS] Baseline mismatch for {key}: have={have} msg={msg.baselineId}. Requesting resync.");
                RequestResync(key, have);
                return;
            }

            EasChunkOpsCodec.ApplyToChunk(chunk, msg.ops);
            _baseline[key] = msg.newBaselineId;

            OnChunkChanged?.Invoke(key);
        }

        private void OnChunkUnsub(ChunkUnsubscribeMsg msg)
        {
            var key = new ChunkCoord(msg.cx, msg.cy, msg.cz);
            _chunks.Remove(key);
            _baseline.Remove(key);
            // optionally: destroy chunk mesh
        }

        private void RequestResync(ChunkCoord key, uint haveBaseline)
        {
            if (!NetworkClient.active) return;
            NetworkClient.Send(new ChunkResyncRequestMsg
            {
                cx = key.x,
                cy = key.y,
                cz = key.z,
                haveBaselineId = haveBaseline
            });
        }
    }
}
