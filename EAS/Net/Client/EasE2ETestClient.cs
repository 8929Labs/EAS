// Observes client chunk-change events and samples received values for range validation.
// This is a runtime diagnostic component; it removes its event subscription on destruction.

// Assets/Scripts/EAS/Net/Client/EasE2ETestClient.cs
using System.Collections.Generic;
using UnityEngine;

namespace EAS.Net
{
    public sealed class EasE2ETestClient : MonoBehaviour
    {
        public EasNetClient netClient;

        [Header("Validation")]
        public bool validateValueRange = true;
        public int sampleChecksPerChunk = 64;

        private void Awake()
        {
            if (netClient == null) netClient = FindAnyObjectByType<EasNetClient>();
            if (netClient == null) Debug.LogError("[EAS-TEST] EasNetClient not found. Ensure EasNetBootstrap is in the scene.");

            if (netClient != null)
            {
                netClient.OnChunkChanged -= OnChunkChanged;
                netClient.OnChunkChanged += OnChunkChanged;
            }
        }

        private void OnDestroy()
        {
            if (netClient != null) netClient.OnChunkChanged -= OnChunkChanged;
        }

        private void OnChunkChanged(ChunkCoord key)
        {
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogOps)
                Debug.Log($"[EAS-TEST] Client chunk changed {key}");

            if (!validateValueRange) return;
            if (!netClient.TryGetChunk(key.x, key.y, key.z, out var chunk)) return;

            int n = chunk.CellCount;
            int checks = Mathf.Min(sampleChecksPerChunk, n);

            // deterministic-ish sampling
            int stride = Mathf.Max(1, n / checks);
            for (int i = 0; i < checks; i++)
            {
                int idx = (i * stride) % n;
                byte v = chunk.Get(idx);
                if (v > 14)
                {
                    Debug.LogError($"[EAS-TEST] FAIL: value out of range at {key} idx={idx} v={v}");
                    return;
                }
            }

            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log($"[EAS-TEST] PASS: range ok for {key} (sampled {checks})");
        }
    }
}
