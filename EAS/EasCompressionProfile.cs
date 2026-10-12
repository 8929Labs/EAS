// Assets/Scripts/EAS/EasCompressionProfile.cs
namespace EAS
{
    /// <summary>
    /// Controls how aggressively auto-compression tests multiple codecs.
    /// </summary>
    public enum EasCompressionProfile : byte
    {
        /// <summary>Try None first, then (optionally) LZ4 and Deflate.</summary>
        Fast = 0,
        /// <summary>Try all enabled codecs and pick smallest.</summary>
        Smallest = 1,
    }
}
