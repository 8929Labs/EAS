// Assets/Scripts/EAS/Net/Client/EasChunkOpsCodec.cs
using System;
using System.Collections.Generic;

namespace EAS.Net
{
    /// <summary>
    /// Ops format (O1):
    /// Header:
    ///   'O','1'
    ///   cellCount (u16 LE)
    ///   runCount (VarUInt)
    /// Body per run:
    ///   startDelta (VarUInt) from previous run start (first is absolute)
    ///   runLength  (VarUInt)
    ///   packedValues (runLength * 4 bits) (values 0..14)
    /// </summary>
    public static class EasChunkOpsCodec
    {
        private const byte M0 = (byte)'O';
        private const byte M1 = (byte)'1';

        private static readonly Comparer<int> IntComparer = Comparer<int>.Default;

        /// <summary>
        /// Encode ops from a chunk's touched indices.
        /// This does NOT require a base chunk: server can send this without storing per-client snapshots.
        /// </summary>
        public static byte[] EncodeFromTouched(EAS.EasChunk chunk, List<int> tempTouched, bool clearTouched = true)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            int n = chunk.CellCount;

            tempTouched ??= new List<int>(256);
            tempTouched.Clear();
            chunk.GetTouched(tempTouched, clear: clearTouched);

            var w = new ByteWriter(64);
            w.WriteU8(M0); w.WriteU8(M1);
            w.WriteU16LE((ushort)n);

            if (tempTouched.Count == 0)
            {
                w.WriteVarUInt(0);
                return w.ToArray();
            }

            tempTouched.Sort(IntComparer);

            int runCountPos = w.ReserveVarUInt();
            int runCount = 0;

            int prevRunStart = 0;
            bool hasPrev = false;

            int i = 0;
            while (i < tempTouched.Count)
            {
                int start = tempTouched[i];
                int length = 1;
                i++;

                // extend run across contiguous indices (only those touched are included)
                while (i < tempTouched.Count)
                {
                    int next = tempTouched[i];
                    if (next != start + length) break;
                    length++;
                    i++;
                }

                int startDelta = hasPrev ? (start - prevRunStart) : start;
                hasPrev = true;
                prevRunStart = start;

                w.WriteVarUInt((uint)startDelta);
                w.WriteVarUInt((uint)length);

                // pack values (4 bits each)
                var bw = new EAS.EasBitWriter(initialBytes: (length * 4 + 7) / 8);
                for (int k = 0; k < length; k++)
                {
                    byte v = chunk.Get(start + k);
                    bw.WriteBits(v, 4);
                }
                w.WriteBytes(bw.ToArray());

                runCount++;
            }

            w.PatchReservedVarUInt(runCountPos, (uint)runCount);
            return w.ToArray();
        }

        /// <summary>
        /// Apply ops onto an existing chunk (in-place). This is fast and avoids reallocating whole chunks.
        /// </summary>
        public static void ApplyToChunk(EAS.EasChunk chunk, byte[] opsBytes)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (opsBytes == null) throw new ArgumentNullException(nameof(opsBytes));
            if (opsBytes.Length < 4) throw new ArgumentException("Ops too short.");

            var r = new ByteReader(opsBytes);
            if (r.ReadU8() != M0 || r.ReadU8() != M1) throw new ArgumentException("Bad ops magic.");

            int cellCount = r.ReadU16LE();
            if (cellCount != chunk.CellCount) throw new ArgumentException("Ops cellCount mismatch.");

            uint runCount = r.ReadVarUInt();
            if (runCount == 0) return;

            int runStart = 0;
            bool hasPrev = false;

            for (uint run = 0; run < runCount; run++)
            {
                int startDelta = (int)r.ReadVarUInt();
                int length = (int)r.ReadVarUInt();
                if (length <= 0) throw new ArgumentException("Bad run length.");

                runStart = hasPrev ? (runStart + startDelta) : startDelta;
                hasPrev = true;

                int packedLen = (length * 4 + 7) / 8;
                byte[] packed = r.ReadBytes(packedLen);

                var br = new EAS.EasBitReader(packed);
                for (int k = 0; k < length; k++)
                {
                    byte v = (byte)br.ReadBits(4);
                    chunk.Set(runStart + k, v);
                }
            }

            // IMPORTANT: applying ops will mark touched indices in the chunk because Set() does that.
            // For client-side, you usually *don't* want to re-broadcast those touches locally.
            // So clear touched after apply:
            chunk.ClearTouched();
            chunk.ClearDirty();
        }

        // ---------- small IO helpers ----------

        private sealed class ByteWriter
        {
            private byte[] _b;
            private int _p;

            public ByteWriter(int cap) { _b = new byte[Math.Max(16, cap)]; _p = 0; }

            public void WriteU8(byte v) { Ensure(1); _b[_p++] = v; }
            public void WriteU16LE(ushort v) { Ensure(2); _b[_p++] = (byte)v; _b[_p++] = (byte)(v >> 8); }
            public void WriteBytes(byte[] src) { Ensure(src.Length); Buffer.BlockCopy(src, 0, _b, _p, src.Length); _p += src.Length; }

            public void WriteVarUInt(uint v)
            {
                while (v >= 0x80) { WriteU8((byte)(v | 0x80)); v >>= 7; }
                WriteU8((byte)v);
            }

            // Reserve max 5 bytes for varuint (uint32)
            public int ReserveVarUInt()
            {
                int pos = _p;
                Ensure(5);
                _b[_p++] = 0; _b[_p++] = 0; _b[_p++] = 0; _b[_p++] = 0; _b[_p++] = 0;
                return pos;
            }

            public void PatchReservedVarUInt(int pos, uint value)
            {
                Span<byte> tmp = stackalloc byte[5];
                int len = 0;
                uint v = value;
                while (v >= 0x80) { tmp[len++] = (byte)(v | 0x80); v >>= 7; }
                tmp[len++] = (byte)v;

                int oldLen = 5;
                int delta = oldLen - len;
                if (delta > 0)
                {
                    Buffer.BlockCopy(_b, pos + oldLen, _b, pos + len, _p - (pos + oldLen));
                    _p -= delta;
                }

                for (int i = 0; i < len; i++) _b[pos + i] = tmp[i];
            }

            private void Ensure(int add)
            {
                if (_p + add <= _b.Length) return;
                Array.Resize(ref _b, Math.Max(_b.Length * 2, _p + add));
            }

            public byte[] ToArray()
            {
                var o = new byte[_p];
                Buffer.BlockCopy(_b, 0, o, 0, _p);
                return o;
            }
        }

        private sealed class ByteReader
        {
            private readonly byte[] _b;
            private int _p;
            public ByteReader(byte[] b) { _b = b; _p = 0; }
            public byte ReadU8() { if (_p >= _b.Length) throw new ArgumentException("Read past end."); return _b[_p++]; }
            public ushort ReadU16LE() { byte a = ReadU8(); byte c = ReadU8(); return (ushort)(a | (c << 8)); }
            public uint ReadVarUInt()
            {
                uint v = 0; int shift = 0;
                while (true)
                {
                    byte b = ReadU8();
                    v |= (uint)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return v;
                    shift += 7;
                    if (shift > 28) throw new ArgumentException("VarUInt too large.");
                }
            }
            public byte[] ReadBytes(int len)
            {
                if (_p + len > _b.Length) throw new ArgumentException("Read past end.");
                var o = new byte[len];
                Buffer.BlockCopy(_b, _p, o, 0, len);
                _p += len;
                return o;
            }
        }
    }
}
