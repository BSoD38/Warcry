using System;
using System.Text;

namespace Warcry.Game;

/// <summary>
/// A stable 64-bit identity for a character name, comparable without allocating.
/// </summary>
/// <remarks>
/// <para>The name-matching side of the audience filter runs inside the ActionEffect
/// detour, where <c>NameString</c> — which allocates a managed string per cast — is not
/// affordable. Hashing the raw UTF-8 bytes in place costs a few nanoseconds and yields a
/// value the config can hold precomputed, so a named-player check is a <c>ulong</c>
/// compare per cast however long the list is.</para>
/// <para>Pair with the home world for the actual match. A 64-bit hash of a name up to 32
/// bytes has no realistic collision risk on its own, but the world is free to carry and
/// it is the field that distinguishes two genuinely different players who chose the same
/// name on different worlds.</para>
/// <para>ASCII letters are folded to lower case so a hand-typed "alice smith" matches
/// "Alice Smith". Folding stops at ASCII deliberately: names carry apostrophes and
/// accented characters, and a locale-aware fold would have to agree byte-for-byte between
/// here and the detour to be worth anything. Non-ASCII therefore matches exactly, which
/// is why the UI offers "add from a recent event" — it captures the exact bytes.</para>
/// </remarks>
public static class PlayerId
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Longest name the game will produce, plus room for the terminator.</summary>
    public const int MaxNameBytes = 64;

    /// <summary>
    /// Hashes a raw name buffer, stopping at the NUL terminator. Zero for an empty name,
    /// which never matches a list entry.
    /// </summary>
    public static ulong Of(ReadOnlySpan<byte> nameUtf8)
    {
        var hash = FnvOffsetBasis;
        var length = 0;

        foreach (var raw in nameUtf8)
        {
            if (raw == 0)
            {
                break;
            }

            // ASCII-only fold. See the type remarks.
            var b = raw is >= (byte)'A' and <= (byte)'Z' ? (byte)(raw + 32) : raw;

            hash ^= b;
            hash *= FnvPrime;
            length++;
        }

        return length == 0 ? 0UL : hash;
    }

    /// <summary>
    /// Hashes a managed name, for config entries the user typed. Not for the cast path.
    /// </summary>
    public static ulong Of(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return 0UL;
        }

        Span<byte> buffer = stackalloc byte[MaxNameBytes];
        if (!Encoding.UTF8.TryGetBytes(trimmed, buffer, out var written))
        {
            // Longer than any name the game can hold, so it can never match one.
            return 0UL;
        }

        return Of(buffer[..written]);
    }
}
