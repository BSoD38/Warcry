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

    /// <summary>Header @0x08. BossMod builds its ActionID from this field.</summary>
    public readonly uint ActionId;

    /// <summary>
    /// Header @0x1C. DeathRecap looks up the Action sheet with THIS for normal actions;
    /// DamageInfoPlugin calls it AnimationId. Carried alongside ActionId until the
    /// Diagnostics tab settles which one is the right mapping key. See docs/PLAN.md 5.1.
    /// </summary>
    public readonly ushort SpellId;

    public readonly byte AnimationVariation;
    public readonly uint GlobalSequence;
    public readonly ushort SourceSequence;
    public readonly byte RawActionType;
    public readonly byte NumTargets;

    public readonly Vector3 Position;

    /// <summary>The game's own Player/Party/Other classification, from Character+0x2369.</summary>
    public readonly byte SoundCategory;

    public readonly CasterKey Caster;
    public readonly bool IsLocalPlayer;
    public readonly TriggerPhase Phase;

    /// <summary>Was the caster's cast bar still running at snapshot?</summary>
    public readonly bool WasCasting;

    public readonly float CastCurrent;
    public readonly float CastTotal;

    /// <summary>
    /// Seconds of cast bar still to run at snapshot — i.e. the MEASURED offset between
    /// this event and the cast visually finishing. If this is consistently non-zero for
    /// cast spells, we can schedule playback off it directly and never ask the user to
    /// tune a latency-dependent constant.
    /// </summary>
    public float CastRemaining => this.WasCasting ? System.MathF.Max(0f, this.CastTotal - this.CastCurrent) : 0f;

    public CastEvent(
        uint casterEntityId, nint casterAddress, uint actionId, ushort spellId,
        byte animationVariation, uint globalSequence, ushort sourceSequence,
        byte rawActionType, byte numTargets, Vector3 position, byte soundCategory,
        CasterKey caster, bool isLocalPlayer, TriggerPhase phase,
        bool wasCasting, float castCurrent, float castTotal)
    {
        this.WasCasting = wasCasting;
        this.CastCurrent = castCurrent;
        this.CastTotal = castTotal;
        this.CasterEntityId = casterEntityId;
        this.CasterAddress = casterAddress;
        this.ActionId = actionId;
        this.SpellId = spellId;
        this.AnimationVariation = animationVariation;
        this.GlobalSequence = globalSequence;
        this.SourceSequence = sourceSequence;
        this.RawActionType = rawActionType;
        this.NumTargets = numTargets;
        this.Position = position;
        this.SoundCategory = soundCategory;
        this.Caster = caster;
        this.IsLocalPlayer = isLocalPlayer;
        this.Phase = phase;
    }
}
