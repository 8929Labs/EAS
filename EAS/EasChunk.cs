// Stores chunk values in packed nibbles and tracks value counts, dirty state, and touched cells.
// The touched set supports sparse network updates; bulk application defers count rebuilding.

// Assets/Scripts/EAS/EasChunk.cs
using System;
using System.Collections.Generic;

namespace EAS
{
    public sealed class EasChunk
    {
        public readonly int CellCount;

        private readonly EasNibbleArray _values;

        private readonly ushort[] _counts = new ushort[15];
        private int _uniqueCount;
        private byte _uniformValueOr255;
        // Bulk apply mode: skip per-Set bookkeeping and rebuild once at end.
        private bool _bulkApplying;

        public bool Dirty { get; private set; }

        // ---- edit-log / touched tracking ----
        // Bitset marking touched indices; plus list of touched indices (no duplicates).
        private readonly uint[] _touchedBits;
        private readonly List<int> _touchedList;

        public EasChunk(int cellCount, byte initialValue /*0..14*/)
        {
            if (cellCount <= 0) throw new ArgumentOutOfRangeException(nameof(cellCount));
            if (initialValue > 14) throw new ArgumentOutOfRangeException(nameof(initialValue), "Value must be 0..14.");

            CellCount = cellCount;
            _values = new EasNibbleArray(cellCount);

            for (int i = 0; i < cellCount; i++) _values.Set(i, initialValue);

            _counts[initialValue] = (ushort)cellCount;
            _uniqueCount = 1;
            _uniformValueOr255 = initialValue;
            Dirty = true;

            _touchedBits = new uint[(cellCount + 31) >> 5];
            _touchedList = new List<int>(128);
        }

        public static EasChunk FromDenseBytes(int cellCount, byte[] denseNibbleBytes)
        {
            if (denseNibbleBytes == null) throw new ArgumentNullException(nameof(denseNibbleBytes));
            int expected = (cellCount + 1) >> 1;
            if (denseNibbleBytes.Length != expected)
                throw new ArgumentException($"Dense data length mismatch. Expected {expected} bytes.", nameof(denseNibbleBytes));

            var c = new EasChunk(cellCount, 0);
            Buffer.BlockCopy(denseNibbleBytes, 0, c._values.RawBytes, 0, expected);

            Array.Clear(c._counts, 0, c._counts.Length);
            c._uniqueCount = 0;

            for (int i = 0; i < cellCount; i++)
            {
                byte v = c._values.Get(i);
                if (v > 14) throw new ArgumentException("Decoded value out of range 0..14.");
                if (c._counts[v] == 0) c._uniqueCount++;
                c._counts[v]++;
            }

            c._uniformValueOr255 = (c._uniqueCount == 1) ? c.FindOnlyValue() : (byte)255;
            c.Dirty = false;

            // loaded chunk has no touched edits
            c.ClearTouched();
            return c;
        }

        public byte Get(int index)
        {
            if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
            return _values.Get(index);
        }

        public void Set(int index, byte newValue /*0..14*/)
        {
            if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
            if (newValue > 14) throw new ArgumentOutOfRangeException(nameof(newValue), "Value must be 0..14.");

            byte old = _values.Get(index);
            if (old == newValue) return;

            _values.Set(index, newValue);
            Dirty = true;

            // Update histogram
            ushort oldCount = _counts[old];
            _counts[old] = (ushort)(oldCount - 1);
            if (oldCount == 1) _uniqueCount--;

            ushort newCount = _counts[newValue];
            _counts[newValue] = (ushort)(newCount + 1);
            if (newCount == 0) _uniqueCount++;

            _uniformValueOr255 = (_uniqueCount == 1) ? FindOnlyValue() : (byte)255;

            // Track touched index (no duplicates)
            MarkTouched(index);
        }

        private void MarkTouched(int index)
        {
            int word = index >> 5;
            uint mask = 1u << (index & 31);
            uint prev = _touchedBits[word];
            if ((prev & mask) != 0) return; // already touched
            _touchedBits[word] = prev | mask;
            _touchedList.Add(index);
        }

        private byte FindOnlyValue()
        {
            for (byte v = 0; v <= 14; v++)
            {
                if (_counts[v] != 0) return v;
            }
            return 255;
        }

        public int UniqueCount => _uniqueCount;
        public byte UniformValueOr255 => _uniformValueOr255;

        public ushort GetCount(byte value0to14) => _counts[value0to14];

        public byte[] DenseRawBytes => _values.RawBytes;

        public void ClearDirty() => Dirty = false;

        // ---- touched APIs ----

        /// <summary>
        /// Copies touched indices into dst and optionally clears touched state.
        /// No allocations if dst is reused by caller.
        /// </summary>
        public void GetTouched(List<int> dst, bool clear = true)
        {
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            dst.AddRange(_touchedList);
            if (clear) ClearTouched();
        }

        /// <summary>Number of indices touched since last clear.</summary>
        public int TouchedCount => _touchedList.Count;

        /// <summary>Clears touched state without affecting Dirty.</summary>
        public void ClearTouched()
        {
            // Clear bitset only for indices we set (fast)
            for (int i = 0; i < _touchedList.Count; i++)
            {
                int idx = _touchedList[i];
                int word = idx >> 5;
                uint mask = 1u << (idx & 31);
                _touchedBits[word] &= ~mask;
            }
            _touchedList.Clear();
        }
    }
}
