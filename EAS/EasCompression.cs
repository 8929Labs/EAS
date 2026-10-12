// Assets/Scripts/EAS/EasCompression.cs
// Updated to route LZ4 through EasLz4Fast when available (allocation-light).
using System;
using System.IO;
using System.IO.Compression;

namespace EAS
{
    public static class EasCompression
    {
        public static byte[] Compress(byte[] input, EasCompressionType type)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            switch (type)
            {
                case EasCompressionType.None:
                    return input;
                case EasCompressionType.Deflate:
                    return DeflateCompress(input);
                case EasCompressionType.LZ4:
                    return EasLz4Fast.Compress(input);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown compression type.");
            }
        }

        public static byte[] Decompress(byte[] input, EasCompressionType type, int expectedUncompressedBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (expectedUncompressedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedUncompressedBytes));

            switch (type)
            {
                case EasCompressionType.None:
                    if (input.Length != expectedUncompressedBytes)
                        throw new ArgumentException($"Uncompressed payload length mismatch. Expected {expectedUncompressedBytes}, got {input.Length}.");
                    return input;

                case EasCompressionType.Deflate:
                    return DeflateDecompress(input, expectedUncompressedBytes);

                case EasCompressionType.LZ4:
                    return EasLz4Fast.Decompress(input, expectedUncompressedBytes);

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown compression type.");
            }
        }

        private static byte[] DeflateCompress(byte[] input)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    ds.Write(input, 0, input.Length);
                }
                return ms.ToArray();
            }
        }

        private static byte[] DeflateDecompress(byte[] input, int expectedBytes)
        {
            var output = new byte[expectedBytes];
            using (var src = new MemoryStream(input))
            using (var ds = new DeflateStream(src, CompressionMode.Decompress))
            {
                int total = 0;
                while (total < expectedBytes)
                {
                    int read = ds.Read(output, total, expectedBytes - total);
                    if (read == 0) break;
                    total += read;
                }
                if (total != expectedBytes)
                    throw new InvalidDataException($"Deflate decompressed {total} bytes, expected {expectedBytes}.");
            }
            return output;
        }
    }
}
