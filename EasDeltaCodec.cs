// Assets/Scripts/EAS/EasDeltaCodec.cs
using System;
using System.Collections.Generic;

namespace EAS
{
    /// <summary>
    /// Delta format (v1):
    /// Header:
    ///   'D','1'
    ///   cellCount (ushort LE)
    ///   entryCount (ushort LE)  // number of edit runs
    /// Body:
    ///   repeated entryCount times:
    ///     startIndex (VarUInt)
    ///     runLength  (VarUInt)  // >=1
    ///     packedValues (runLength * 4 bits) // new values, 0..14
    ///
    /// Indices are absolute (not deltas) to keep decode simple.
    /// Runs are contiguous indices. Single edits are runLength=1.
    /// </summary>
    public static class EasDeltaCodec
    {
        private const byte D0 = (byte)'D';
        private const byte D1 = (byte)'1';

        // Tune: if edits are very sparse, you can increase this to avoid grouping across gaps.
        public static int MaxGapToMergeRuns = 0; // 0 => only truly contiguous.

        public static byte[] EncodeDelta(EasChunk baseChunk, EasChunk newChunk)
        {
            if (baseChunk == null) throw new ArgumentNullException(nameof(baseChunk));
            if (newChunk == null) throw new ArgumentNullException(nameof(newChunk));
            if (baseChunk.CellCount != newChunk.CellCount) throw new ArgumentException("CellCount mismatch.");

            int n = newChunk.CellCount;

            // Find runs of changed cells.
            var runs = new List<(int start, int length)>(64);
            int i = 0;
            while (i < n)
            {
                if (baseChunk.Get(i) == newChunk.Get(i)) { i++; continue; }

                int start = i;
                int len = 1;
                i++;

                while (i < n)
                {
                    bool changed = baseChunk.Get(i) != newChunk.Get(i);
                    if (!changed)
                    {
                        // Optionally merge across small gaps (disabled by default).
                        int gapStart = i;
                        int gap = 0;
                        while (i < n && baseChunk.Get(i) == newChunk.Get(i) && gap <= MaxGapToMergeRuns) { gap++; i++; }
                        if (gap <= MaxGapToMergeRuns && i < n && baseChunk.Get(i) != newChunk.Get(i))
                        {
                            // Merge: include the gap as edits too (we'll write new values, which equal base there).
                            len += gap;
                            continue;
                        }
                        // stop at first non-mergeable equal cell
                        i = gapStart;
                        break;
                    }
                    len++;
                    i++;
                }

                runs.Add((start, len));
            }

            // Build bytes
            var w = new ByteWriter(64);
            w.WriteU8(D0);
            w.WriteU8(D1);
            w.WriteU16LE((ushort)n);
            w.WriteU16LE((ushort)runs.Count);

            foreach (var (start, length) in runs)
            {
                w.WriteVarUInt((uint)start);
                w.WriteVarUInt((uint)length);

                // Packed 4-bit values for this run
                var bw = new EasBitWriter(initialBytes: (length * 4 + 7) / 8);
                for (int k = 0; k < length; k++)
                {
                    byte v = newChunk.Get(start + k);
                    if (v > 14) throw new ArgumentException("Value out of range 0..14.");
                    bw.WriteBits(v, 4);
                }
                var packed = bw.ToArray();
                w.WriteBytes(packed);
            }

            return w.ToArray();
        }

        /// <summary>
        /// Applies a delta onto a base chunk, producing a NEW chunk (dense editable).
        /// </summary>
        public static EasChunk ApplyDelta(EasChunk baseChunk, byte[] deltaBytes)
        {
            if (baseChunk == null) throw new ArgumentNullException(nameof(baseChunk));
            if (deltaBytes == null) throw new ArgumentNullException(nameof(deltaBytes));
            if (deltaBytes.Length < 6) throw new ArgumentException("Delta too short.");

            var r = new ByteReader(deltaBytes);
            if (r.ReadU8() != D0 || r.ReadU8() != D1) throw new ArgumentException("Bad delta magic.");

            int cellCount = r.ReadU16LE();
            if (cellCount != baseChunk.CellCount) throw new ArgumentException("Delta cellCount mismatch.");

            int entryCount = r.ReadU16LE();

            // Start from base dense bytes
            int denseLen = (cellCount + 1) >> 1;
            var dense = new byte[denseLen];
            Buffer.BlockCopy(baseChunk.DenseRawBytes, 0, dense, 0, denseLen);

            for (int e = 0; e < entryCount; e++)
            {
                int start = (int)r.ReadVarUInt();
                int length = (int)r.ReadVarUInt();
                if (start < 0 || start >= cellCount) throw new ArgumentOutOfRangeException(nameof(start));
                if (length <= 0 || start + length > cellCount) throw new ArgumentOutOfRangeException(nameof(length));

                int packedLen = (length * 4 + 7) / 8;
                var packed = r.ReadBytes(packedLen);

                var br = new EasBitReader(packed);
                for (int k = 0; k < length; k++)
                {
                    byte v = (byte)br.ReadBits(4);
                    if (v > 14) throw new ArgumentException("Delta value out of range 0..14.");
                    SetNibble(dense, start + k, v);
                }
            }

            return EasChunk.FromDenseBytes(cellCount, dense);
        }

        // --- tiny IO helpers (no allocations besides final array) ---

        private sealed class ByteWriter
        {
            private byte[] _b;
            private int _p;
            public ByteWriter(int cap) { _b = new byte[Math.Max(16, cap)]; }
            public void WriteU8(byte v) { Ensure(1); _b[_p++] = v; }
            public void WriteU16LE(ushort v) { Ensure(2); _b[_p++] = (byte)v; _b[_p++] = (byte)(v >> 8); }
            public void WriteBytes(byte[] src) { Ensure(src.Length); Buffer.BlockCopy(src, 0, _b, _p, src.Length); _p += src.Length; }
            public void WriteVarUInt(uint v)
            {
                // LEB128
                while (v >= 0x80) { WriteU8((byte)(v | 0x80)); v >>= 7; }
                WriteU8((byte)v);
            }
            private void Ensure(int add) { if (_p + add > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, _p + add)); }
            public byte[] ToArray() { var o = new byte[_p]; Buffer.BlockCopy(_b, 0, o, 0, _p); return o; }
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
