// Reads chunk header metadata for diagnostics without decoding the voxel payload.
// The returned Info reports format details, sizes, and parsing errors.

// Assets/Scripts/EAS/EasHeaderPeek.cs
using System;

namespace EAS
{
    public static class EasHeaderPeek
    {
        public readonly struct Info
        {
            public readonly bool ok;

            public readonly byte versionRaw; // 1 or 'S'
            public readonly int headerBytes;

            public readonly EasEncodingType encoding;
            public readonly EasCompressionType compression;

            public readonly int cellCount;

            // v2 only (for v1 this is -1)
            public readonly int expectedUncompressedPayloadBytes;

            // payload bytes present in the blob (compressed size in v2, raw size in v1)
            public readonly int payloadBytes;

            public readonly int totalBytes;

            public readonly string error;

            public Info(
              bool ok,
              byte versionRaw,
              int headerBytes,
              EasEncodingType encoding,
              EasCompressionType compression,
              int cellCount,
              int expectedUncompressedPayloadBytes,
              int payloadBytes,
              int totalBytes,
              string error
            )
            {
                this.ok = ok;
                this.versionRaw = versionRaw;
                this.headerBytes = headerBytes;
                this.encoding = encoding;
                this.compression = compression;
                this.cellCount = cellCount;
                this.expectedUncompressedPayloadBytes = expectedUncompressedPayloadBytes;
                this.payloadBytes = payloadBytes;
                this.totalBytes = totalBytes;
                this.error = error;
            }

            public override string ToString()
            {
                if (!ok) return $"EAS Peek: FAIL ({error})";

                string ver = versionRaw == 1 ? "v1" : (versionRaw == (byte)'S' ? "v2" : $"v?(0x{versionRaw:X2})");
                if (versionRaw == 1)
                {
                    return $"EAS {ver} cells={cellCount} enc={encoding} comp=None payload={payloadBytes}B total={totalBytes}B";
                }

                return $"EAS {ver} cells={cellCount} enc={encoding} comp={compression} ulen={expectedUncompressedPayloadBytes}B payload={payloadBytes}B total={totalBytes}B";
            }
        }

        public static bool TryPeek(byte[] encoded, out Info info)
        {
            if (encoded == null)
            {
                info = Fail("encoded is null", 0);
                return false;
            }

            if (encoded.Length < 6)
            { // minimum for v1
                info = Fail("too short (<6)", encoded.Length);
                return false;
            }

            if (encoded[0] != (byte)'E' || encoded[1] != (byte)'A')
            {
                info = Fail("bad magic (expected 'E','A')", encoded.Length);
                return false;
            }

            byte ver = encoded[2];

            // v1
            if (ver == 1)
            {
                if (encoded.Length < 6)
                {
                    info = Fail("too short for v1 header", encoded.Length);
                    return false;
                }

                var type = (EasEncodingType)encoded[3];
                int cellCount = (ushort)(encoded[4] | (encoded[5] << 8));

                int headerBytes = 6;
                int payloadBytes = encoded.Length - headerBytes;

                info = new Info(
                  ok: true,
                  versionRaw: ver,
                  headerBytes: headerBytes,
                  encoding: type,
                  compression: EasCompressionType.None,
                  cellCount: cellCount,
                  expectedUncompressedPayloadBytes: -1,
                  payloadBytes: payloadBytes,
                  totalBytes: encoded.Length,
                  error: ""
                );
                return true;
            }

            // v2 (VERSION_V2 is byte 'S')
            if (ver == (byte)'S')
            {
                if (encoded.Length < 11)
                {
                    info = Fail("too short for v2 header (<11)", encoded.Length);
                    return false;
                }

                var type = (EasEncodingType)encoded[3];
                var comp = (EasCompressionType)encoded[4];

                int ulen =
                  (encoded[5]) |
                  (encoded[6] << 8) |
                  (encoded[7] << 16) |
                  (encoded[8] << 24);

                int cellCount = (ushort)(encoded[9] | (encoded[10] << 8));

                int headerBytes = 11;
                int payloadBytes = encoded.Length - headerBytes;
                if (payloadBytes < 0) payloadBytes = 0;

                info = new Info(
                  ok: true,
                  versionRaw: ver,
                  headerBytes: headerBytes,
                  encoding: type,
                  compression: comp,
                  cellCount: cellCount,
                  expectedUncompressedPayloadBytes: ulen,
                  payloadBytes: payloadBytes,
                  totalBytes: encoded.Length,
                  error: ""
                );
                return true;
            }

            info = Fail($"unsupported version byte 0x{ver:X2}", encoded.Length);
            return false;
        }

        public static string PeekToString(byte[] encoded)
        {
            TryPeek(encoded, out var info);
            return info.ToString();
        }

        private static Info Fail(string error, int totalBytes)
        {
            return new Info(
              ok: false,
              versionRaw: 0,
              headerBytes: 0,
              encoding: default,
              compression: default,
              cellCount: 0,
              expectedUncompressedPayloadBytes: -1,
              payloadBytes: 0,
              totalBytes: totalBytes,
              error: error
            );
        }
    }
}
