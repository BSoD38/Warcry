using System.Numerics;
using Dalamud.Plugin.Services;

namespace Warcry.Game;

/// <summary>Answers "where is this caster, right now", by entity id.</summary>
/// <remarks>
/// <para>By id every time, never a cached object. A game-object reference held across
/// frames outlives the actor it points at, and an actor can despawn mid-line.</para>
/// <para>Framework thread only — it reads the object table.</para>
/// </remarks>
public sealed class CasterLocator
{
    private readonly IObjectTable objects;

    public CasterLocator(IObjectTable objects) => this.objects = objects;

    /// <summary>The caster's live world position, or false if it is not in the table.</summary>
    /// <remarks>
    /// NOTE: SearchByEntityId, not SearchById. CastEvent carries the 32-bit ENTITY id;
    /// SearchById takes the 64-bit object id and would silently find nothing.
    /// </remarks>
    public bool TryGetPosition(uint entityId, out Vector3 position)
    {
        var actor = entityId == 0 ? null : this.objects.SearchByEntityId(entityId);
        position = actor?.Position ?? default;
        return actor is not null;
    }
}
