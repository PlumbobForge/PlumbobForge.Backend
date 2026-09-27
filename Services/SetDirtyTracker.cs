using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public static class SetDirtyTracker
{
    public static string ComputeContentHash(string folderName, IEnumerable<MetaEntity> items)
    {
        var ordered = items.OrderBy(i => i.Id).ToList();
        var sb = new StringBuilder();
        sb.Append("DIR:").Append(folderName).Append(';');
        foreach (var item in ordered)
        {
            sb.Append(item.Id)
              .Append(':')
              .Append(item.FileName)
              .Append(':')
              .Append(item.Filehash)
              .Append(':')
              .Append(item.Enabled ? "1" : "0")
              .Append(';');
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    public static string ComputeContentHash(SetsEntity set)
    {
        return ComputeContentHash(set.FolderName ?? set.Name, set.MetaEntities ?? Enumerable.Empty<MetaEntity>());
    }

    public static void UpdateDirtyState(SetsEntity set)
    {
        var currentHash = ComputeContentHash(set);
        set.Dirty = string.IsNullOrEmpty(set.CachedHash) || !string.Equals(set.CachedHash, currentHash, StringComparison.Ordinal);
    }

    public static async Task UpdateDirtyStatesForSetsAsync(AppDbContext db, IEnumerable<long> setIds)
    {
        var idList = setIds.Distinct().ToList();
        if (idList.Count == 0) return;

        var sets = await db.SetsEntities
            .Include(s => s.MetaEntities)
            .Where(s => idList.Contains(s.Id))
            .ToListAsync();

        foreach (var set in sets)
        {
            UpdateDirtyState(set);
        }
    }
}
