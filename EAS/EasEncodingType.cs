// Identifies how voxel values are represented before optional payload compression.
// The numeric identifiers form part of the persisted format and must remain stable.

// Assets/Scripts/EAS/EasEncodingType.cs
namespace EAS
{
    public enum EasEncodingType : byte
    {
        Uniform = 0,
        Palette = 1,
        Dense = 2,
        Bitplane = 3,
    }
}
