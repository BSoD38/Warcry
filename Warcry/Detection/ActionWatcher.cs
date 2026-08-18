using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Warcry.Game;
using CSObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using SNVector3 = System.Numerics.Vector3;

namespace Warcry.Detection;

/// <summary>
/// Owns the action-detection hooks and turns raw packet payloads into <see cref="CastEvent"/>.
/// </summary>
/// <remarks>
/// <para>Hooks exactly one function: <c>ActionEffectHandler.Receive</c>, the funnel for the
/// ActionEffect1/8/16/24/32 packets. It fires once per action, identically for local and
/// remote casters, so no deduplication is needed while it is the only trigger source.</para>
/// <para>The detour must never allocate, lock, do I/O or await. The one exception is the
/// caster-name capture, which is gated behind <see cref="CaptureNames"/> and exists only
/// so the Events tab can show who cast what.</para>
/// </remarks>
public sealed unsafe class ActionWatcher : IDisposable
{
    public delegate void CastHandler(in CastEvent ev, DropStage drop, string casterName);

    private const int FaultLimit = 20;

    private readonly Hook<ActionEffectHandler.Delegates.Receive>? receiveHook;
    private readonly CastHandler onCast;
    private readonly VoiceSlotTable slots;
    private readonly IPluginLog log;
    private readonly Func<uint> localEntityId;

    private int faults;
    private volatile bool tripped;

    public bool Installed => this.receiveHook is not null;

    public bool Tripped => this.tripped;

    public nint HookAddress { get; }

    /// <summary>Capture caster names for the Events tab. Allocates in the detour.</summary>
    public bool CaptureNames { get; set; } = true;

    public ActionWatcher(
        IGameInteropProvider interop,
        IPluginLog log,
        VoiceSlotTable slots,
        Func<uint> localEntityId,
        CastHandler onCast)
    {
        this.log = log;
        this.slots = slots;
        this.localEntityId = localEntityId;
        this.onCast = onCast;

        try
        {
            this.HookAddress = ActionEffectHandler.Addresses.Receive.Value;
            this.receiveHook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                this.HookAddress, this.ReceiveDetour);
            this.receiveHook.Enable();
            log.Information("ActionWatcher: hooked ActionEffectHandler.Receive at 0x{Address:X}", this.HookAddress);
        }
        catch (Exception ex)
        {
            // Patch-day signature failure must be inert, not a crash and not a log flood.
            // The Status tab reads Installed and explains itself to the user.
            this.receiveHook = null;
            log.Error(ex, "ActionWatcher: could not resolve ActionEffectHandler.Receive. Detection is disabled.");
        }
    }

    private void ReceiveDetour(
        uint casterEntityId,
        Character* casterPtr,
        SNVector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        // Call Original FIRST so game behaviour never depends on our success.
        this.receiveHook!.OriginalDisposeSafe(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        if (this.tripped)
        {
            return;
        }

        try
        {
            // Not a ClientStructs contract — the "always non-null" claim is one plugin
            // author's comment. Guarding is a couple of nanoseconds.
            if (casterPtr == null || header == null)
            {
                return;
            }

            var drop = DropStage.None;

            if (casterPtr->GameObject.ObjectKind != CSObjectKind.Pc)
            {
                drop = DropStage.NotPc;
            }
            else if ((ActionType)header->ActionType != ActionType.Action)
            {
                drop = DropStage.NotAction;
            }

            var caster = CasterKey.None;
            if (drop == DropStage.None)
            {
                ref var cd = ref casterPtr->DrawData.CustomizeData;
                // Mask defensively: ushort in VfxContainer, byte on the wire in SpawnPackets.
                var voiceId = (ushort)(casterPtr->Vfx.VoiceId & 0xFF);
                caster = new CasterKey(
                    cd.Race, cd.Tribe, cd.Sex, voiceId,
                    this.slots.SlotOf(cd.Race, cd.Sex, voiceId));
            }

            // Is the cast bar still running at snapshot? If so, the remaining time IS the
            // offset between this event and the cast visually finishing — measured rather
            // than guessed, and inherently latency-correct. See docs/PLAN.md 5.1.
            var wasCasting = false;
            var castCurrent = 0f;
            var castTotal = 0f;
            if (drop == DropStage.None)
            {
                var bc = (BattleChara*)casterPtr;
                var castInfo = bc->GetCastInfo();
                if (castInfo != null && castInfo->IsCasting)
                {
                    wasCasting = true;
                    castCurrent = castInfo->CurrentCastTime;
                    castTotal = castInfo->TotalCastTime;
                }
            }

            var p = casterPtr->GameObject.Position;

            var ev = new CastEvent(
                casterEntityId: casterEntityId,
                casterAddress: (nint)casterPtr,
                actionId: header->ActionId,
                animationVariation: header->AnimationVariation,
                globalSequence: header->GlobalSequence,
                sourceSequence: header->SourceSequence,
                rawActionType: header->ActionType,
                numTargets: header->NumTargets,
                position: new SNVector3(p.X, p.Y, p.Z),
                soundCategory: casterPtr->SoundVolumeCategory,
                caster: caster,
                // Compare the 32-bit ENTITY id. Never use SourceSequence as an identity
                // test: it is 0 for some of your own actions (NIN mudras).
                isLocalPlayer: casterEntityId == this.localEntityId(),
                phase: TriggerPhase.Snapshot,
                wasCasting: wasCasting,
                castCurrent: castCurrent,
                castTotal: castTotal);

            var name = this.CaptureNames ? casterPtr->GameObject.NameString : string.Empty;

            this.onCast(in ev, drop, name);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Warcry pipeline threw inside the ActionEffect detour");
            if (Interlocked.Increment(ref this.faults) > FaultLimit)
            {
                // Keep the hook installed but inert. Safer than unhooking mid-frame.
                this.tripped = true;
                this.log.Error("Warcry: {Limit} pipeline faults — detection disabled until reload.", FaultLimit);
            }
        }
    }

    public void Dispose()
    {
        this.receiveHook?.Disable();
        this.receiveHook?.Dispose();
    }
}
