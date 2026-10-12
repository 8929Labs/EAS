// Identifies the second-stage compression applied to an encoded chunk payload.
// These byte values are stored in chunk headers and must remain stable for existing data.

// Assets/Scripts/EAS/EasCompressionType.cs
namespace EAS
{
    public enum EasCompressionType : byte
    {
        None = 0,
        Deflate = 1,
        LZ4 = 2,
    }
}
