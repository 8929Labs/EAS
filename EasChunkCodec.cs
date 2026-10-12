// Assets/Scripts/EAS/EasChunkCodec.cs
// Changes:
// - Uses v2 header (already in your previous version)
// - Adds Auto compression selection (None/LZ4/Deflate) via EasAutoCompression
// - Keeps decode compatible with v1 + v2
using System;

namespace EAS
{
    public static class EasChunkCodec
    {
        private const byte MAGIC0 = (byte)'E';
        private const byte MAGIC1 = (byte)'A';
        private const byte VERSION_V1 = 1;
        private const byte VERSION_V2 = (byte)'S';

        public static bool AllowBitplaneForDisk = false;

        // If true, will try None/LZ4/Deflate and pick best.
        public static bool UseAutoCompression = true;

        // If auto is off, this is used.
        public static EasCompressionType DefaultCompressionForDisk = EasCompressionType.Deflate;

        public static EasCompressionProfile AutoProfile = EasCompressionProfile.Fast;

        public static byte[] EncodeForDisk(EasChunk chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));

            int paletteSize;
            var type = EasHeuristics.ChooseForDisk(chunk, out paletteSize, allowBitplane: AllowBitplaneForDisk);

            // Build UNCOMPRESSED payload first
            byte[] uncompressedPayload = type switch
            {
                EasEncodingType.Uniform => EncodeUniform(chunk),
                EasEncodingType.Palette => EncodePalette(chunk, paletteSize),
                EasEncodingType.Dense => EncodeDense(chunk),
                EasEncodingType.Bitplane => EncodeBitplane(chunk),
                _ => EncodeDense(chunk),
            };

            EasCompressionType comp;
            byte[] payload;

            if (UseAutoCompression)
            {
                (comp, payload) = EasAutoCompression.ChooseBest(uncompressedPayload, AutoProfile);
            }
            else
            {
                comp = DefaultCompressionForDisk;
                payload = EasCompression.Compress(uncompressedPayload, comp);
            }

            // Header v2:
            // 2 magic + 1 ver + 1 type + 1 comp + 4 uncompressedLen + 2 cellCount = 11 bytes
            int headerSize = 11;
            var data = new byte[headerSize + payload.Length];

            data[0] = MAGIC0;
            data[1] = MAGIC1;
            data[2] = VERSION_V2;
            data[3] = (byte)type;
            data[4] = (byte)comp;

            uint ulen = (uint)uncompressedPayload.Length;
            data[5] = (byte)(ulen & 0xFF);
            data[6] = (byte)((ulen >> 8) & 0xFF);
            data[7] = (byte)((ulen >> 16) & 0xFF);
            data[8] = (byte)((ulen >> 24) & 0xFF);

            ushort cc = (ushort)chunk.CellCount;
            data[9] = (byte)(cc & 0xFF);
            data[10] = (byte)((cc >> 8) & 0xFF);

            Buffer.BlockCopy(payload, 0, data, headerSize, payload.Length);
            return data;
        }

        public static EasChunk DecodeFromDisk(byte[] encoded)
        {
            if (encoded == null) throw new ArgumentNullException(nameof(encoded));
            if (encoded.Length < 6) throw new ArgumentException("Data too short.", nameof(encoded));
            if (encoded[0] != MAGIC0 || encoded[1] != MAGIC1) throw new ArgumentException("Bad magic.", nameof(encoded));

            byte version = encoded[2];

            if (version == VERSION_V1)
            {
                var type = (EasEncodingType)encoded[3];
                ushort cellCount = (ushort)(encoded[4] | (encoded[5] << 8));
                int payloadOffset = 6;
                int payloadLen = encoded.Length - payloadOffset;

                return DecodePayload(type, EasCompressionType.None, cellCount, encoded, payloadOffset, payloadLen, payloadLen);
            }

            if (version == VERSION_V2)
            {
                if (encoded.Length < 11) throw new ArgumentException("Data too short for v2 header.", nameof(encoded));

                var type = (EasEncodingType)encoded[3];
                var comp = (EasCompressionType)encoded[4];

                int ulen =
                  (encoded[5]) |
                  (encoded[6] << 8) |
                  (encoded[7] << 16) |
                  (encoded[8] << 24);

                ushort cellCount = (ushort)(encoded[9] | (encoded[10] << 8));

                int payloadOffset = 11;
                int payloadLen = encoded.Length - payloadOffset;

                return DecodePayload(type, comp, cellCount, encoded, payloadOffset, payloadLen, ulen);
            }

            throw new ArgumentException($"Unsupported version {version}.", nameof(encoded));
        }

        private static EasChunk DecodePayload(
          EasEncodingType type,
          EasCompressionType compression,
          int cellCount,
          byte[] data,
          int payloadOffset,
          int payloadLen,
          int expectedUncompressedPayloadBytes
        )
        {
            var compressed = new byte[payloadLen];
            Buffer.BlockCopy(data, payloadOffset, compressed, 0, payloadLen);

            byte[] payload = EasCompression.Decompress(compressed, compression, expectedUncompressedPayloadBytes);

            return type switch
            {
                EasEncodingType.Uniform => DecodeUniform(cellCount, payload, 0, payload.Length),
                EasEncodingType.Palette => DecodePalette(cellCount, payload, 0, payload.Length),
                EasEncodingType.Dense => DecodeDense(cellCount, payload, 0, payload.Length),
                EasEncodingType.Bitplane => DecodeBitplane(cellCount, payload, 0, payload.Length),
                _ => throw new ArgumentException("Unknown encoding type."),
            };
        }

        // --------- payload encoders/decoders (same as your prior versions) ---------

        private static byte[] EncodeUniform(EasChunk chunk)
        {
            byte v = chunk.UniformValueOr255;
            if (v == 255) v = chunk.Get(0);
            return new byte[] { v };
        }

        private static EasChunk DecodeUniform(int cellCount, byte[] data, int off, int len)
        {
            if (len < 1) throw new ArgumentException("Uniform payload too short.");
            byte v = data[off];
            if (v > 14) throw new ArgumentException("Uniform value out of range 0..14.");
            var c = new EasChunk(cellCount, v);
            c.ClearDirty();
            return c;
        }

        private static byte[] EncodeDense(EasChunk chunk)
        {
            int bytes = (chunk.CellCount + 1) >> 1;
            var payload = new byte[bytes];
            Buffer.BlockCopy(chunk.DenseRawBytes, 0, payload, 0, bytes);
            return payload;
        }

        private static EasChunk DecodeDense(int cellCount, byte[] data, int off, int len)
        {
            int expected = (cellCount + 1) >> 1;
            if (len != expected) throw new ArgumentException($"Dense payload length mismatch. Expected {expected}.");
            var dense = new byte[expected];
            Buffer.BlockCopy(data, off, dense, 0, expected);
            return EasChunk.FromDenseBytes(cellCount, dense);
        }

        private static byte[] EncodePalette(EasChunk chunk, int paletteSize)
        {
            if (paletteSize < 2 || paletteSize > 15) return EncodeDense(chunk);
            int bitsPerIndex = EasHeuristics.BitsForPaletteIndex(paletteSize);

            var palette = new byte[paletteSize];
            var valueToIndex = new byte[15];
            for (int i = 0; i < 15; i++) valueToIndex[i] = 255;

            int p = 0;
            for (byte v = 0; v <= 14; v++)
            {
                if (chunk.GetCount(v) != 0)
                {
                    palette[p] = v;
                    valueToIndex[v] = (byte)p;
                    p++;
                }
            }

            var bw = new EasBitWriter(initialBytes: (chunk.CellCount * bitsPerIndex + 7) / 8);
            for (int i = 0; i < chunk.CellCount; i++)
            {
                byte v = chunk.Get(i);
                bw.WriteBits(valueToIndex[v], bitsPerIndex);
            }
            byte[] packed = bw.ToArray();

            var payload = new byte[2 + paletteSize + packed.Length];
            payload[0] = (byte)paletteSize;
            payload[1] = (byte)bitsPerIndex;
            Buffer.BlockCopy(palette, 0, payload, 2, paletteSize);
            Buffer.BlockCopy(packed, 0, payload, 2 + paletteSize, packed.Length);
            return payload;
        }

        private static EasChunk DecodePalette(int cellCount, byte[] data, int off, int len)
        {
            if (len < 2) throw new ArgumentException("Palette payload too short.");

            int paletteSize = data[off + 0];
            int bitsPerIndex = data[off + 1];

            if (paletteSize < 1 || paletteSize > 15) throw new ArgumentException("Bad paletteSize.");
            if (paletteSize == 1)
            {
                if (len < 3) throw new ArgumentException("Palette payload too short for paletteSize=1.");
                byte v = data[off + 2];
                var c = new EasChunk(cellCount, v);
                c.ClearDirty();
                return c;
            }

            if (bitsPerIndex < 1 || bitsPerIndex > 4) throw new ArgumentException("Bad bitsPerIndex.");

            int paletteOff = off + 2;
            if (len < 2 + paletteSize) throw new ArgumentException("Palette payload too short for palette.");

            var palette = new byte[paletteSize];
            Buffer.BlockCopy(data, paletteOff, palette, 0, paletteSize);

            int indicesOff = paletteOff + paletteSize;
            int indicesLen = (off + len) - indicesOff;
            var packed = new byte[indicesLen];
            Buffer.BlockCopy(data, indicesOff, packed, 0, indicesLen);

            var br = new EasBitReader(packed);
            var dense = new byte[(cellCount + 1) >> 1];
            for (int i = 0; i < cellCount; i++)
            {
                uint idx = br.ReadBits(bitsPerIndex);
                if (idx >= (uint)paletteSize) throw new ArgumentException("Palette index out of range.");
                SetNibble(dense, i, palette[idx]);
            }
            return EasChunk.FromDenseBytes(cellCount, dense);
        }

        private static byte[] EncodeBitplane(EasChunk chunk)
        {
            int planeBytes = (chunk.CellCount + 7) >> 3;
            var payload = new byte[15 * planeBytes];
            for (int i = 0; i < chunk.CellCount; i++)
            {
                byte v = chunk.Get(i);
                int byteIndex = i >> 3;
                int bitIndex = i & 7;
                payload[v * planeBytes + byteIndex] |= (byte)(1 << bitIndex);
            }
            return payload;
        }

        private static EasChunk DecodeBitplane(int cellCount, byte[] data, int off, int len)
        {
            int planeBytes = (cellCount + 7) >> 3;
            int expected = 15 * planeBytes;
            if (len != expected) throw new ArgumentException($"Bitplane payload length mismatch. Expected {expected}.");

            var dense = new byte[(cellCount + 1) >> 1];

            for (int i = 0; i < cellCount; i++)
            {
                int byteIndex = i >> 3;
                int bitMask = 1 << (i & 7);

                byte found = 255;
                for (byte v = 0; v <= 14; v++)
                {
                    int planeOffset = off + v * planeBytes;
                    if ((data[planeOffset + byteIndex] & bitMask) != 0)
                    {
                        if (found != 255) throw new ArgumentException($"Bitplane decode: multiple set at cell {i}.");
                        found = v;
                    }
                }
                if (found == 255) throw new ArgumentException($"Bitplane decode: none set at cell {i}.");

                SetNibble(dense, i, found);
            }

            return EasChunk.FromDenseBytes(cellCount, dense);
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
