// Encodes D2 differences from the replacement chunk touched set instead of scanning all cells.
// Touched cells that match the baseline are omitted; run starts use relative offsets.

// Assets/Scripts/EAS/EasDeltaCodec2.cs
using System;
using System.Collections.Generic;

namespace EAS
{
    public static class EasDeltaCodec2
    {
        private const byte M0 = (byte)'D';
        private const byte M1 = (byte)'2';

        // Temp list reused to avoid GC if you pass one in.
        private static readonly Comparer<int> IntComparer = Comparer<int>.Default;

        /// <summary>
        /// Encodes a delta using only newChunk's touched indices (O(k)).
        /// Any touched indices that ended up equal to base are skipped.
        /// </summary>
        public static byte[] EncodeDeltaFromTouched(
          EasChunk baseChunk,
          EasChunk newChunk,
          List<int> tempTouched /*can be null*/,
          bool clearTouched = true
        )
        {
            if (baseChunk == null) throw new ArgumentNullException(nameof(baseChunk));
            if (newChunk == null) throw new ArgumentNullException(nameof(newChunk));
            if (baseChunk.CellCount != newChunk.CellCount) throw new ArgumentException("CellCount mismatch.");

            int n = newChunk.CellCount;

            tempTouched ??= new List<int>(256);
            tempTouched.Clear();

            newChunk.GetTouched(tempTouched, clear: clearTouched);
            if (tempTouched.Count == 0)
            {
                // Minimal empty delta
                var w0 = new ByteWriter(8);
                w0.WriteU8(M0); w0.WriteU8(M1);
                w0.WriteU16LE((ushort)n);
                w0.WriteVarUInt(0);
                return w0.ToArray();
            }

            tempTouched.Sort(IntComparer);

            // Build runs over only indices that actually differ from base
            var w = new ByteWriter(64);
            w.WriteU8(M0); w.WriteU8(M1);
            w.WriteU16LE((ushort)n);

            // We don't know runCount up front; reserve a spot by writing later:
            int runCountPos = w.ReserveVarUInt();
            int runCount = 0;

            int prevRunStart = 0;
            bool hasPrev = false;

            int i = 0;
            while (i < tempTouched.Count)
            {
                int idx = tempTouched[i];

                // Skip if not actually changed vs base
                if (baseChunk.Get(idx) == newChunk.Get(idx)) { i++; continue; }

                int start = idx;
                int length = 1;
                i++;

                // Extend run across contiguous touched indices that differ
                while (i < tempTouched.Count)
                {
                    int next = tempTouched[i];
                    if (next != start + length) break;

                    if (baseChunk.Get(next) != newChunk.Get(next))
                    {
                        length++;
                    }
                    i++;
                }

                // Write run header
                int startDelta = hasPrev ? (start - prevRunStart) : start;
                hasPrev = true;
                prevRunStart = start;

                w.WriteVarUInt((uint)startDelta);
                w.WriteVarUInt((uint)length);

                // Pack values (4 bits each)
                var bw = new EasBitWriter(initialBytes: (length * 4 + 7) / 8);
                for (int k = 0; k < length; k++)
                {
                    byte v = newChunk.Get(start + k);
                    bw.WriteBits(v, 4);
                }
                w.WriteBytes(bw.ToArray());

                runCount++;
            }

            w.PatchReservedVarUInt(runCountPos, (uint)runCount);
            return w.ToArray();
        }

        /// <summary>
        /// Applies a D2 delta to baseChunk and returns a new editable chunk.
        /// </summary>
        public static EasChunk ApplyDelta(EasChunk baseChunk, byte[] deltaBytes)
        {
            if (baseChunk == null) throw new ArgumentNullException(nameof(baseChunk));
            if (deltaBytes == null) throw new ArgumentNullException(nameof(deltaBytes));
            if (deltaBytes.Length < 4) throw new ArgumentException("Delta too short.");

            var r = new ByteReader(deltaBytes);
            if (r.ReadU8() != M0 || r.ReadU8() != M1) throw new ArgumentException("Bad delta magic.");

            int cellCount = r.ReadU16LE();
            if (cellCount != baseChunk.CellCount) throw new ArgumentException("Delta cellCount mismatch.");

            uint runCount = r.ReadVarUInt();

            // Start from base dense bytes
            int denseLen = (cellCount + 1) >> 1;
            var dense = new byte[denseLen];
            Buffer.BlockCopy(baseChunk.DenseRawBytes, 0, dense, 0, denseLen);

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
                var packed = r.ReadBytes(packedLen);

                var br = new EasBitReader(packed);
                for (int k = 0; k < length; k++)
                {
                    byte v = (byte)br.ReadBits(4);
                    SetNibble(dense, runStart + k, v);
                }
            }

            return EasChunk.FromDenseBytes(cellCount, dense);
        }

        // -------- helpers --------

        private sealed class ByteWriter
        {
            private byte[] _b;
            private int _p;

            // For reserve/patch VarUInt
            private int _reservePos = -1;
            private int _reserveLen = 0;

            public ByteWriter(int cap) { _b = new byte[Math.Max(16, cap)]; _p = 0; }

            public void WriteU8(byte v) { Ensure(1); _b[_p++] = v; }
            public void WriteU16LE(ushort v) { Ensure(2); _b[_p++] = (byte)v; _b[_p++] = (byte)(v >> 8); }
            public void WriteBytes(byte[] src) { Ensure(src.Length); Buffer.BlockCopy(src, 0, _b, _p, src.Length); _p += src.Length; }

            public void WriteVarUInt(uint v)
            {
                while (v >= 0x80) { WriteU8((byte)(v | 0x80)); v >>= 7; }
                WriteU8((byte)v);
            }

            // Reserve a VarUInt spot (we’ll patch later). We reserve the max 5 bytes (for uint32).
            public int ReserveVarUInt()
            {
                _reservePos = _p;
                _reserveLen = 5;
                Ensure(_reserveLen);
                for (int i = 0; i < _reserveLen; i++) _b[_p++] = 0;
                return _reservePos;
            }

            public void PatchReservedVarUInt(int pos, uint value)
            {
                // Encode to temp
                Span<byte> tmp = stackalloc byte[5];
                int len = 0;
                uint v = value;
                while (v >= 0x80) { tmp[len++] = (byte)(v | 0x80); v >>= 7; }
                tmp[len++] = (byte)v;

                // Move tail if needed
                int oldLen = 5;
                int delta = oldLen - len;
                if (delta > 0)
                {
                    // shift left
                    Buffer.BlockCopy(_b, pos + oldLen, _b, pos + len, _p - (pos + oldLen));
                    _p -= delta;
                }
                else if (delta < 0)
                {
                    // shift right (shouldn’t happen since oldLen is max)
                    Ensure(-delta);
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

        private static void SetNibble(byte[] nibbleBytes, int index, byte value0to15)
        {
            int i = index >> 1;
            byte b = nibbleBytes[i];
            if ((index & 1) == 0) nibbleBytes[i] = (byte)((b & 0xF0) | (value0to15 & 0x0F));
            else nibbleBytes[i] = (byte)((b & 0x0F) | ((value0to15 & 0x0F) << 4));
        }
    }
}
