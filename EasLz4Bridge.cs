// Assets/Scripts/EAS/EasLz4Bridge.cs
// Only compiled if you add the dependency and define EAS_LZ4.
#if EAS_LZ4
using System;
using K4os.Compression.LZ4;

namespace EAS {
  public static class EasLz4Bridge {
    // Format: [uncompressedLength:int32 LE][lz4Bytes...]
    // We store uncompressed length in the chunk header already, but this makes the blob self-contained too.
    public static byte[] Compress(byte[] input) {
      if (input == null) throw new ArgumentNullException(nameof(input));

      int max = LZ4Codec.MaximumOutputSize(input.Length);
      var tmp = new byte[max];
      int written = LZ4Codec.Encode(input, 0, input.Length, tmp, 0, tmp.Length, LZ4Level.L12_MAX);

      var outBuf = new byte[4 + written];
      outBuf[0] = (byte)(input.Length & 0xFF);
      outBuf[1] = (byte)((input.Length >> 8) & 0xFF);
      outBuf[2] = (byte)((input.Length >> 16) & 0xFF);
      outBuf[3] = (byte)((input.Length >> 24) & 0xFF);

      Buffer.BlockCopy(tmp, 0, outBuf, 4, written);
      return outBuf;
    }

    public static byte[] Decompress(byte[] input, int expectedUncompressedBytes) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      if (input.Length < 4) throw new ArgumentException("LZ4 blob too short.");

      int declared =
        (input[0]) |
        (input[1] << 8) |
        (input[2] << 16) |
        (input[3] << 24);

      if (declared != expectedUncompressedBytes)
        throw new ArgumentException($"LZ4 declared {declared} bytes, expected {expectedUncompressedBytes}.");

      var output = new byte[expectedUncompressedBytes];
      int decoded = LZ4Codec.Decode(input, 4, input.Length - 4, output, 0, output.Length);

      if (decoded != expectedUncompressedBytes)
        throw new InvalidOperationException($"LZ4 decoded {decoded} bytes, expected {expectedUncompressedBytes}.");

      return output;
    }
  }
}
#endif
