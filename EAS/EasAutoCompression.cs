// Selects a payload compression codec by comparing enabled candidates against raw bytes.
// Savings thresholds prevent small reductions from justifying extra compression work.

// Assets/Scripts/EAS/EasAutoCompression.cs
using System;

namespace EAS
{
    public static class EasAutoCompression
    {
        /// <summary>
        /// If true and LZ4 is available, auto-compress can choose LZ4.
        /// </summary>
        public static bool AllowLz4 = true;

        /// <summary>
        /// If true, auto-compress can choose Deflate.
        /// </summary>
        public static bool AllowDeflate = true;

        /// <summary>
        /// Minimum bytes saved required to pick a compressed blob over None (avoids wasting CPU for tiny wins).
        /// </summary>
        public static int MinBytesSavedToCompress = 16;

        /// <summary>
        /// Minimum percent saved required to pick a compressed blob over None.
        /// </summary>
        public static int MinPercentSavedToCompress = 2;

        public static (EasCompressionType type, byte[] payload) ChooseBest(
          byte[] uncompressedPayload,
          EasCompressionProfile profile
        )
        {
            if (uncompressedPayload == null) throw new ArgumentNullException(nameof(uncompressedPayload));

            // Baseline
            var bestType = EasCompressionType.None;
            var best = uncompressedPayload;
            int bestLen = uncompressedPayload.Length;

            // Helper to accept a candidate if it beats best and clears thresholds.
            void Consider(EasCompressionType t, byte[] candidate)
            {
                if (candidate == null) return;
                int len = candidate.Length;
                if (len >= bestLen) return;

                int saved = bestLen - len;
                int savedPct = (bestLen == 0) ? 0 : (saved * 100) / bestLen;

                if (t != EasCompressionType.None)
                {
                    if (saved < MinBytesSavedToCompress) return;
                    if (savedPct < MinPercentSavedToCompress) return;
                }

                bestType = t;
                best = candidate;
                bestLen = len;
            }

            // Fast profile:
            // - Always allow None
            // - Optionally try LZ4 (cheap)
            // - Only try Deflate if chunk is "big enough" or profile is Smallest
            bool tryLz4 = AllowLz4;
            bool tryDeflate = AllowDeflate && (profile == EasCompressionProfile.Smallest || uncompressedPayload.Length >= 512);

            // LZ4
            if (tryLz4)
            {
                try
                {
                    var lz4 = EasCompression.Compress(uncompressedPayload, EasCompressionType.LZ4);
                    Consider(EasCompressionType.LZ4, lz4);
                }
                catch
                {
                    // If LZ4 not installed / not enabled, ignore.
                }
            }

            // Deflate
            if (tryDeflate)
            {
                var def = EasCompression.Compress(uncompressedPayload, EasCompressionType.Deflate);
                Consider(EasCompressionType.Deflate, def);
            }

            return (bestType, best);
        }
    }
}
