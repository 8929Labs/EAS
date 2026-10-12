// Assets/Scripts/EAS/Net/Client/ChunkCoord.cs
namespace EAS.Net
{
    public readonly struct ChunkCoord
    {
        public readonly int x, y, z;
        public ChunkCoord(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }

        public override int GetHashCode() => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
        public override bool Equals(object obj) => obj is ChunkCoord c && c.x == x && c.y == y && c.z == z;
        public override string ToString() => $"({x},{y},{z})";
    }
}
