// Ensures the scene has an EAS network client and forwards chunk-change notifications.
// Early execution order initializes the client hook before typical scene components.

// Assets/Scripts/EAS/Net/Client/EasNetBootstrap.cs
using UnityEngine;

namespace EAS.Net
{
    /// <summary>
    /// Client bootstrap: ensures an EasNetClient exists and provides a single hook for chunk changed events.
    /// Put this in your client scene (or a shared scene) on a persistent GameObject.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class EasNetBootstrap : MonoBehaviour
    {
        [Tooltip("If null, will FindObjectOfType<EasNetClient>() or create one.")]
        public EasNetClient client;

        [Header("Client hooks")]
        [Tooltip("Log chunk updates to console (debug).")]
        public bool logChunkUpdates = false;

        private void Awake()
        {
            if (client == null) client = FindAnyObjectByType<EasNetClient>();
            if (client == null) client = gameObject.AddComponent<EasNetClient>();
            if (EAS.EasDebug.Enabled && EAS.EasDebug.LogNetworking)
                Debug.Log("[EAS] EasNetBootstrap Awake (client-side object exists)");

            // Hook chunk change callback
            client.OnChunkChanged -= OnChunkChanged;
            client.OnChunkChanged += OnChunkChanged;

            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (client != null) client.OnChunkChanged -= OnChunkChanged;
        }

        private void OnChunkChanged(ChunkCoord key)
        {
            if (logChunkUpdates) Debug.Log($"[EAS] Chunk changed {key}");

            // TODO: mesh rebuild trigger point
            // Example:
            // YourChunkMeshSystem.Instance.RequestRebuild(key);
        }
    }
}
