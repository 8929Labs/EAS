// Provides optional pointer-based LZ4 paths and Unity NativeArray overloads.
// Pooled temporary buffers reduce allocation pressure; returned managed blobs still own their bytes.

// Assets/Scripts/EAS/EasLz4Fast.cs
// "Burst-friendly fast paths" in practice means:
// 1) do all heavy per-cell packing (palette/dense/bitplane/delta building) in Burst jobs
// 2) keep the actual compression call allocation-free and pointer-based on the main thread
//
// IMPORTANT: Calling K4os LZ4 from Burst jobs is generally NOT Burst-compatible.
// This file provides *allocation-free*, *pointer-based* LZ4 wrappers you can call from main thread,
// and NativeArray-friendly overloads for minimized copying.
//
using System;
using System.Buffers;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace EAS
{
    public static class EasLz4Fast
    {
#if EAS_LZ4
        /// <summary>
        /// Compresses input using pointer-based K4os LZ4 and returns a tightly-sized byte[].
        /// Format: [uncompressedLen:int32 LE][lz4...]
        /// </summary>
        public static unsafe byte[] Compress(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            int srcLen = input.Length;
            int maxOut = K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(srcLen);

            byte[] tmp = ArrayPool<byte>.Shared.Rent(maxOut);
            try
            {
                fixed (byte* src = input)
                fixed (byte* dst = tmp)
                {
                    int written = K4os.Compression.LZ4.LZ4Codec.Encode(
                      src, srcLen,
                      dst, maxOut,
                      K4os.Compression.LZ4.LZ4Level.L12_MAX
                    );

                    var outBuf = new byte[4 + written];
                    outBuf[0] = (byte)(srcLen & 0xFF);
                    outBuf[1] = (byte)((srcLen >> 8) & 0xFF);
                    outBuf[2] = (byte)((srcLen >> 16) & 0xFF);
                    outBuf[3] = (byte)((srcLen >> 24) & 0xFF);
                    Buffer.BlockCopy(tmp, 0, outBuf, 4, written);
                    return outBuf;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }

        public static unsafe byte[] Decompress(byte[] input, int expectedUncompressedLen)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Length < 4) throw new ArgumentException("LZ4 blob too short.");

            int declared =
              (input[0]) |
              (input[1] << 8) |
              (input[2] << 16) |
              (input[3] << 24);

            if (declared != expectedUncompressedLen)
                throw new ArgumentException($"LZ4 declared {declared} bytes, expected {expectedUncompressedLen}.");

            var output = new byte[expectedUncompressedLen];

            fixed (byte* src = input)
            fixed (byte* dst = output)
            {
                int decoded = K4os.Compression.LZ4.LZ4Codec.Decode(
                  src + 4, input.Length - 4,
                  dst, expectedUncompressedLen
                );
                if (decoded != expectedUncompressedLen)
                    throw new InvalidOperationException($"LZ4 decoded {decoded} bytes, expected {expectedUncompressedLen}.");
            }

            return output;
        }

        /// <summary>
        /// Compresses a NativeArray<byte> into a managed byte[] without intermediate copies (besides the final output).
        /// Call this from the main thread after a Burst job produced the uncompressed payload.
        /// </summary>
        public static unsafe byte[] Compress(NativeArray<byte> input)
        {
            if (!input.IsCreated) throw new ArgumentException("Input not created.");

            int srcLen = input.Length;
            int maxOut = K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(srcLen);

            byte[] tmp = ArrayPool<byte>.Shared.Rent(maxOut);
            try
            {
                byte* src = (byte*)input.GetUnsafeReadOnlyPtr();
                fixed (byte* dst = tmp)
                {
                    int written = K4os.Compression.LZ4.LZ4Codec.Encode(
                      src, srcLen,
                      dst, maxOut,
                      K4os.Compression.LZ4.LZ4Level.L12_MAX
                    );

                    var outBuf = new byte[4 + written];
                    outBuf[0] = (byte)(srcLen & 0xFF);
                    outBuf[1] = (byte)((srcLen >> 8) & 0xFF);
                    outBuf[2] = (byte)((srcLen >> 16) & 0xFF);
                    outBuf[3] = (byte)((srcLen >> 24) & 0xFF);
                    Buffer.BlockCopy(tmp, 0, outBuf, 4, written);
                    return outBuf;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
#else
    public static byte[] Compress(byte[] input) =>
      throw new NotSupportedException("Enable EAS_LZ4 and add K4os.Compression.LZ4 to use this.");
    public static byte[] Decompress(byte[] input, int expectedUncompressedLen) =>
      throw new NotSupportedException("Enable EAS_LZ4 and add K4os.Compression.LZ4 to use this.");
    public static byte[] Compress(NativeArray<byte> input) =>
      throw new NotSupportedException("Enable EAS_LZ4 and add K4os.Compression.LZ4 to use this.");
#endif
    }
}
