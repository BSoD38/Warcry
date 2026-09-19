using System;
using System.Text;

namespace Warcry.Game;

// A stable 64-bit identity for a character name, comparable without allocating: the
// name-matching side of the audience filter runs inside the ActionEffect detour, where
// NameString's per-cast allocation is not affordable. The config holds these precomputed,
// so a named-player check is a ulong compare however long the list is.
// Pair with the home world for the actual match — it distinguishes two players who chose
// the same name on different worlds.
// ASCII letters are folded to lower case so "alice smith" matches "Alice Smith". Folding
// stops at ASCII deliberately: a locale-aware fold would have to agree byte-for-byte with
// the detour to be worth anything, so non-ASCII matches exactly. Hence the UI's "add from
// a recent event", which captures the exact bytes.
public static class PlayerId
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // Longest name the game will produce, plus the terminator.
    public const int MaxNameBytes = 64;

    // Stops at the NUL terminator. Zero for an empty name, which never matches a list entry.
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

            var b = raw is >= (byte)'A' and <= (byte)'Z' ? (byte)(raw + 32) : raw;

            hash ^= b;
            hash *= FnvPrime;
            length++;
        }

        return length == 0 ? 0UL : hash;
    }

    // For config entries the user typed. Not for the cast path.
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
