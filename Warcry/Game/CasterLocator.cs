using System.Numerics;
using Dalamud.Plugin.Services;

namespace Warcry.Game;

// By id every time, never a cached object: a game-object reference held across frames
// outlives the actor it points at. Framework thread only — it reads the object table.
public sealed class CasterLocator
{
    private readonly IObjectTable objects;

    public CasterLocator(IObjectTable objects) => this.objects = objects;

    public bool TryGetPosition(uint entityId, out Vector3 position)
    {
        // SearchByEntityId, not SearchById: CastEvent carries the 32-bit entity id, and
        // SearchById takes the 64-bit object id and would silently find nothing.
        var actor = entityId == 0 ? null : this.objects.SearchByEntityId(entityId);
        position = actor?.Position ?? default;
        return actor is not null;
    }
}
