using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public static class SetHierarchyHelper
{
    public static async Task<HashSet<long>> GetAllDescendantSetIdsAsync(
        AppDbContext db,
        IEnumerable<long>? rootSetIds,
        CancellationToken cancellationToken = default)
    {
        var result = rootSetIds != null ? new HashSet<long>(rootSetIds) : new HashSet<long>();
        if (result.Count == 0) return result;

        var allSets = await db.SetsEntities
            .AsNoTracking()
            .Select(s => new { s.Id, s.ParentSetsEntityId })
            .ToListAsync(cancellationToken);

        var lookup = allSets
            .Where(s => s.ParentSetsEntityId.HasValue)
            .ToLookup(s => s.ParentSetsEntityId!.Value, s => s.Id);

        var queue = new Queue<long>(result);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var childId in lookup[parentId])
            {
                if (result.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }

        return result;
    }
}
