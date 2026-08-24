using System.Numerics;
using Dalamud.Plugin.Services;

namespace Warcry.Game;

/// <summary>Answers "where is this caster, right now", by entity id.</summary>
/// <remarks>
/// <para>By id every time, never a cached object. A game-object reference held across
/// frames outlives the actor it points at, and an actor can despawn mid-line.</para>
/// <para>Framework thread only — it reads the object table.</para>
/// <para>This is the positioning half of the caster seam. v1 only ever asks about the
/// local player, which is what the fast path is for; the table scan exists for v2's remote
/// casters rather than sitting on v1's per-frame path.</para>
/// </remarks>
public sealed class CasterLocator
{
    private readonly IObjectTable objects;

    public CasterLocator(IObjectTable objects) => this.objects = objects;

    /// <summary>The caster's live world position, or false if it is not in the table.</summary>
    public bool TryGetPosition(uint entityId, out Vector3 position)
    {
        if (entityId != 0)
        {
            var local = this.objects.LocalPlayer;
            if (local is not null && local.EntityId == entityId)
            {
                position = local.Position;
                return true;
            }

            // NOTE: SearchByEntityId, not SearchById. CastEvent carries the 32-bit ENTITY
            // id; SearchById takes the 64-bit object id and would silently find nothing.
            var actor = this.objects.SearchByEntityId(entityId);
            if (actor is not null)
            {
                position = actor.Position;
                return true;
            }
        }

        position = default;
        return false;
    }
}
