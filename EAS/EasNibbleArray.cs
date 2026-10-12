// Assets/Scripts/EAS/EasNibbleArray.cs
using System;

namespace EAS
{
    // Stores values 0..15 packed as 4-bit nibbles.
    public sealed class EasNibbleArray
    {
        public readonly int Length;
        private readonly byte[] _data; // Length/2 rounded up

        public EasNibbleArray(int length)
        {
            Length = length;
            _data = new byte[(length + 1) >> 1];
        }

        public byte Get(int index)
        {
            int i = index >> 1;
            byte b = _data[i];
            return (index & 1) == 0 ? (byte)(b & 0x0F) : (byte)(b >> 4);
        }

        public void Set(int index, byte value0to15)
        {
            int i = index >> 1;
            byte b = _data[i];
            if ((index & 1) == 0) _data[i] = (byte)((b & 0xF0) | (value0to15 & 0x0F));
            else _data[i] = (byte)((b & 0x0F) | ((value0to15 & 0x0F) << 4));
        }

        public byte[] RawBytes => _data;
    }
}
