using System.Numerics;
using Warcry.Game;

namespace Warcry.Detection;

// One action use, captured on the game main thread. A readonly struct passed by in: the
// detour must not allocate.
public readonly struct CastEvent
{
    public readonly uint CasterEntityId;

    // Header @0x08, the mapping key: it names the action actually pressed, where SpellId
    // (@0x1C) names the animation.
    public readonly uint ActionId;

    public readonly uint GlobalSequence;

    public readonly Vector3 Position;

    // The game's own Player/Party/Other classification, from Character+0x2369.
    public readonly byte SoundCategory;

    public readonly CasterKey Caster;

    // Captured in the detour so the audience filter never touches the game object again.
    // The name arrives hashed because NameString allocates and the named list is checked
    // every cast. The relation bits come from the ClientStructs properties rather than a
    // RelationFlags mask here, so a struct update is free to move the layout.
    public readonly CasterFacts Facts;

    public bool IsLocalPlayer => this.Facts.IsSelf;

    public readonly bool WasCasting;

    public readonly float CastCurrent;
    public readonly float CastTotal;

    // Most a snapshot can precede its cast bar completing. The slidecast window is
    // 0.40-0.46 s (docs/PLAN.md 5.1); this leaves headroom for a bad connection and stays
    // an order of magnitude below any real cast.
    private const float SlidecastCeilingSeconds = 1.5f;

    // Seconds of cast bar still to run at snapshot — the measured offset to the cast
    // visually finishing, so playback needs no user-tuned latency constant.
    // Zero unless the running bar plausibly belongs to this action: GetCastInfo reports
    // whatever the caster is casting NOW, so an off-GCD woven into a long cast reads the
    // long cast's remaining time and the instant would be held back by seconds, or dropped
    // past the scheduler's ceiling. A magnitude test rather than an action-id comparison
    // because the bar's id and the packet's legitimately differ for an upgraded spell.
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
        uint casterEntityId, uint actionId, uint globalSequence,
        Vector3 position, byte soundCategory,
        CasterKey caster, in CasterFacts facts,
        bool wasCasting, float castCurrent, float castTotal)
    {
        this.Facts = facts;
        this.WasCasting = wasCasting;
        this.CastCurrent = castCurrent;
        this.CastTotal = castTotal;
        this.CasterEntityId = casterEntityId;
        this.ActionId = actionId;
        this.GlobalSequence = globalSequence;
        this.Position = position;
        this.SoundCategory = soundCategory;
        this.Caster = caster;
    }
}
