namespace Meridian.Core;

/// <summary>
/// Deterministic hashing for cache keys. Runtime <c>string.GetHashCode()</c> is randomised per
/// process, so it is useless for a cache key that must be identical across nodes and restarts.
/// FNV-1a over the canonical string gives a stable 64-bit key. A random-Guid cache key is exactly
/// what this exists to prevent.
/// </summary>
public static class StableHash
{
    public static ulong Fnv1a64(ReadOnlySpan<char> text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
