using System.Text;

namespace SkuaSim.Core.World;

/// <summary>
/// Process-independent string hash. <see cref="string.GetHashCode()"/> is randomized per process in .NET,
/// so it must never feed IDs or anything else that has to be identical between runs with the same seed.
/// </summary>
public static class StableHash
{
    /// <summary>32-bit FNV-1a over the UTF-8 bytes of <paramref name="text"/>, returned as a non-negative int.</summary>
    public static int Of(string? text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(text ?? ""))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
