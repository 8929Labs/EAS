// Assets/Scripts/EAS/Net/Client/EasNetMessages.cs
using Mirror;

namespace EAS.Net
{
    public struct ChunkFullMsg : NetworkMessage
    {
        public int cx, cy, cz;
        public uint baselineId;
        public byte[] blob; // full EAS chunk blob (EasChunkCodec.EncodeForDisk)
    }

    public struct ChunkOpsMsg : NetworkMessage
    {
        public int cx, cy, cz;
        public uint baselineId;    // what client should currently have
        public uint newBaselineId; // after applying ops
        public byte[] ops;         // ops blob (EasChunkOpsCodec)
    }

    public struct ChunkUnsubscribeMsg : NetworkMessage
    {
        public int cx, cy, cz;
    }

    // Optional: client -> server request for resync if it detects mismatch
    public struct ChunkResyncRequestMsg : NetworkMessage
    {
        public int cx, cy, cz;
        public uint haveBaselineId;
    }
}
