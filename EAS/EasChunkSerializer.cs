// Persists complete chunk blobs through EasChunkCodec using files or caller-owned streams.
// Successful saves clear the chunk dirty flag; stream loading consumes the remaining bytes.

// Assets/Scripts/EAS/EasChunkSerializer.cs
using System.IO;

namespace EAS
{
    public static class EasChunkSerializer
    {
        // Save one chunk blob to a file (uses EasChunkCodec's current settings: auto-compression or default)
        public static void SaveToFile(string path, EasChunk chunk)
        {
            var bytes = EasChunkCodec.EncodeForDisk(chunk);
            File.WriteAllBytes(path, bytes);
            chunk.ClearDirty();
        }

        // Load one chunk blob from a file.
        public static EasChunk LoadFromFile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return EasChunkCodec.DecodeFromDisk(bytes);
        }

        // Save to any stream
        public static void SaveToStream(Stream stream, EasChunk chunk)
        {
            var bytes = EasChunkCodec.EncodeForDisk(chunk);
            stream.Write(bytes, 0, bytes.Length);
            chunk.ClearDirty();
        }

        public static EasChunk LoadFromStream(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return EasChunkCodec.DecodeFromDisk(ms.ToArray());
            }
        }
    }
}
