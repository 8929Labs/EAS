// Provides matching bit-stream readers and writers for compact voxel values and palette indices.
// Bits are consumed least-significant first; both sides must use the same field widths.

// Assets/Scripts/EAS/EasBitPacker.cs
using System;

namespace EAS
{
    /// <summary>
    /// Fast bit writer: accumulates bits into a 64-bit register and flushes bytes in chunks.
    /// Little-endian bit order: first bit written becomes LSB of first byte.
    /// </summary>
    public sealed class EasBitWriter
    {
        private byte[] _buf;
        private int _bytePos;

        private ulong _bitAcc;
        private int _bitCount; // number of valid bits in _bitAcc

        public EasBitWriter(int initialBytes = 64)
        {
            _buf = new byte[Math.Max(8, initialBytes)];
            _bytePos = 0;
            _bitAcc = 0;
            _bitCount = 0;
        }

        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount <= 0) return;
            if (bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));

            // Mask value to bitCount
            if (bitCount != 32) value &= (1u << bitCount) - 1u;

            // Append to accumulator
            _bitAcc |= ((ulong)value) << _bitCount;
            _bitCount += bitCount;

            // Flush whole bytes
            while (_bitCount >= 8)
            {
                Ensure(1);
                _buf[_bytePos++] = (byte)(_bitAcc & 0xFF);
                _bitAcc >>= 8;
                _bitCount -= 8;
            }
        }

        public byte[] ToArray()
        {
            // Flush remaining partial byte if any
            if (_bitCount > 0)
            {
                Ensure(1);
                _buf[_bytePos++] = (byte)(_bitAcc & 0xFF);
                _bitAcc = 0;
                _bitCount = 0;
            }

            var outBuf = new byte[_bytePos];
            Buffer.BlockCopy(_buf, 0, outBuf, 0, _bytePos);
            return outBuf;
        }

        public int BitLength => (_bytePos * 8) + _bitCount;

        private void Ensure(int addBytes)
        {
            if (_bytePos + addBytes <= _buf.Length) return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _bytePos + addBytes));
        }
    }

    /// <summary>
    /// Fast bit reader: refills a 64-bit register from bytes and serves bits in chunks.
    /// Little-endian bit order matching EasBitWriter.
    /// </summary>
    public sealed class EasBitReader
    {
        private readonly byte[] _buf;
        private int _bytePos;

        private ulong _bitAcc;
        private int _bitCount;

        public EasBitReader(byte[] buf)
        {
            _buf = buf ?? throw new ArgumentNullException(nameof(buf));
            _bytePos = 0;
            _bitAcc = 0;
            _bitCount = 0;
        }

        public uint ReadBits(int bitCount)
        {
            if (bitCount <= 0) return 0;
            if (bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));

            // Refill until enough bits
            while (_bitCount < bitCount)
            {
                if (_bytePos >= _buf.Length)
                    throw new ArgumentException("Read past end of bitstream.");

                _bitAcc |= ((ulong)_buf[_bytePos++]) << _bitCount;
                _bitCount += 8;
            }

            uint value = (uint)(_bitAcc & ((bitCount == 32) ? 0xFFFFFFFFUL : ((1UL << bitCount) - 1UL)));
            _bitAcc >>= bitCount;
            _bitCount -= bitCount;
            return value;
        }
    }
}
