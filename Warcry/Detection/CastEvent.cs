using System.Numerics;
using Warcry.Game;

namespace Warcry.Detection;

/// <summary>Where in an action's life the event was captured.</summary>
public enum TriggerPhase : byte
{
    /// <summary>ActionEffectHandler.Receive — effect application. The default.</summary>
    Snapshot = 0,

    /// <summary>ActionManager.UseActionLocation — local player keypress. Optional.</summary>
    Input = 1,

    /// <summary>PacketDispatcher.HandleActorCastPacket — cast bar start. v2, optional.</summary>
    CastStart = 2,
}

/// <summary>
/// One action use, captured on the game main thread. Deliberately a readonly struct
/// passed by <c>in</c>: the detour must not allocate.
/// </summary>
public readonly struct CastEvent
{
    public readonly uint CasterEntityId;
    public readonly nint CasterAddress;

    /// <summary>
    /// Header @0x08. Settled in game as the mapping key: it consistently names the
    /// action actually pressed, where SpellId (@0x1C) named the animation.
    /// </summary>
    public readonly uint ActionId;

    public readonly byte AnimationVariation;
    public readonly uint GlobalSequence;
    public readonly ushort SourceSequence;
    public readonly byte RawActionType;
    public readonly byte NumTargets;

    public readonly Vector3 Position;

    /// <summary>The game's own Player/Party/Other classification, from Character+0x2369.</summary>
    public readonly byte SoundCategory;

    public readonly CasterKey Caster;
    public readonly TriggerPhase Phase;

    /// <summary>
    /// Who the caster is to you, captured in the detour so the audience filter never has
    /// to touch the game object again.
    /// </summary>
    /// <remarks>
    /// The name arrives hashed rather than as a string: the named-player list is checked on
    /// every cast, and <c>NameString</c> allocates. The relation bits are read through the
    /// ClientStructs properties rather than by masking <c>RelationFlags</c> here, because
    /// the bit layout is the client's and a struct update should be free to move it.
    /// </remarks>
    public readonly CasterFacts Facts;

    /// <summary>Was this you? The 32-bit entity id compare, never SourceSequence.</summary>
    public bool IsLocalPlayer => this.Facts.IsSelf;

    /// <summary>Was the caster's cast bar still running at snapshot?</summary>
    public readonly bool WasCasting;

    public readonly float CastCurrent;
    public readonly float CastTotal;

    /// <summary>
    /// Most a snapshot can precede its own cast bar completing.
    /// </summary>
    /// <remarks>
    /// The slidecast window is latency-dependent, measured at 0.40-0.46 s in game
    /// (docs/PLAN.md 5.1) — generous headroom over that for a bad connection, and still an
    /// order of magnitude below any real cast. That gap is what makes it usable as the test
    /// for whether the running bar is even this action's.
    /// </remarks>
    private const float SlidecastCeilingSeconds = 1.5f;

    /// <summary>
    /// Seconds of cast bar still to run at snapshot — i.e. the MEASURED offset between
    /// this event and the cast visually finishing. If this is consistently non-zero for
    /// cast spells, we can schedule playback off it directly and never ask the user to
    /// tune a latency-dependent constant.
    /// </summary>
    /// <remarks>
    /// Zero unless the running bar plausibly belongs to this action. <c>GetCastInfo</c>
    /// reports whatever the caster is casting <em>now</em>, which need not be what just
    /// snapshotted: an off-GCD ability woven into a long cast reads the LONG CAST's
    /// remaining time, so an instant would be held back by seconds — or, past the
    /// scheduler's ceiling, dropped outright. Because snapshot precedes the bar completing
    /// by one slidecast window and never by seconds, a bar with more than
    /// <see cref="SlidecastCeilingSeconds"/> left is someone else's and earns no offset.
    /// Deliberately a magnitude test and not an action-id comparison: the bar's id and the
    /// packet's may legitimately differ for an upgraded spell, and guessing wrong there
    /// would silently disable the offset for every hard cast.
    /// </remarks>
    public float CastRemaining
    {
        get
        {
            if (!this.WasCasting)
            {
                return 0f;
            }

            var remaining = System.MathF.Max(0f, this.CastTotal - this.CastCurrent);
            return remaining <= SlidecastCeilingSeconds ? remaining : 0f;
        }
    }

    public CastEvent(
        uint casterEntityId, nint casterAddress, uint actionId,
        byte animationVariation, uint globalSequence, ushort sourceSequence,
        byte rawActionType, byte numTargets, Vector3 position, byte soundCategory,
        CasterKey caster, in CasterFacts facts, TriggerPhase phase,
        bool wasCasting, float castCurrent, float castTotal)
    {
        this.Facts = facts;
        this.WasCasting = wasCasting;
        this.CastCurrent = castCurrent;
        this.CastTotal = castTotal;
        this.CasterEntityId = casterEntityId;
        this.CasterAddress = casterAddress;
        this.ActionId = actionId;
        this.AnimationVariation = animationVariation;
        this.GlobalSequence = globalSequence;
        this.SourceSequence = sourceSequence;
        this.RawActionType = rawActionType;
        this.NumTargets = numTargets;
        this.Position = position;
        this.SoundCategory = soundCategory;
        this.Caster = caster;
        this.Phase = phase;
    }
}
