// Chooses a voxel representation from the chunk value distribution.
// Uniform chunks need one value; small palettes reduce index width; dense values use nibbles.

// Assets/Scripts/EAS/EasHeuristics.cs
namespace EAS
{
    public static class EasHeuristics
    {
        // If you want to sometimes emit Bitplane for disk, set allowBitplane = true.
        // Note: Bitplane is usually only "worth it" once you add a second-stage compressor (LZ4/Deflate).
        public static EasEncodingType ChooseForDisk(EasChunk chunk, out int paletteSizeOut, bool allowBitplane = false)
        {
            int unique = chunk.UniqueCount;
            paletteSizeOut = unique;

            if (unique == 1) return EasEncodingType.Uniform;

            if (allowBitplane)
            {
                // Simple "peaky" rule: if one value dominates, bitplanes tend to compress very well later.
                int max = 0;
                for (byte v = 0; v <= 14; v++)
                {
                    int c = chunk.GetCount(v);
                    if (c > max) max = c;
                }
                // 92%+ of one value is a decent trigger for bitplane (mainly when compressed afterwards).
                if (max * 100 >= chunk.CellCount * 92) return EasEncodingType.Bitplane;
            }

            // Palette is only helpful when bitsPerIndex < 4 (i.e., paletteSize <= 8).
            if (unique <= 8) return EasEncodingType.Palette;

            return EasEncodingType.Dense;
        }

        public static int BitsForPaletteIndex(int paletteSize)
        {
            if (paletteSize <= 1) return 0;
            if (paletteSize <= 2) return 1;
            if (paletteSize <= 4) return 2;
            if (paletteSize <= 8) return 3;
            return 4; // 9..15
        }
    }
}
