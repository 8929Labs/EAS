// Assets/Scripts/EAS/EasDebug.cs
namespace EAS
{
    /// <summary>
    /// Central debug flags for the EAS system.
    /// Safe to reference from any layer (storage, net, client, server).
    /// </summary>
    public static class EasDebug
    {
        /// <summary>
        /// Master switch for EAS debug logging.
        /// </summary>
        public static bool Enabled = false;

        /// <summary>
        /// Log codec decisions (encoding + compression).
        /// </summary>
        public static bool LogCodecHeaders = false;

        /// <summary>
        /// Log networking events (subscribe, full send, ops).
        /// </summary>
        public static bool LogNetworking = false;

        /// <summary>
        /// Log ops encoding/apply details.
        /// </summary>
        public static bool LogOps = false;
    }
}
