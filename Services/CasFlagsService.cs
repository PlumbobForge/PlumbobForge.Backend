using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Resources;
using S3ForgeTools.Utils.Logging;

namespace PlumbobForge.Backend.Services;

public sealed record CasFlagSummary(int ItemCount, int CasItemCount, int PartCount, IReadOnlyDictionary<uint, int> PartsWithBit);

/// <param name="SkippedParts">CAS parts left alone because another resource in their package has the same key.</param>
/// <param name="StoppedByGame">The game was started during the run; items after that point were not changed.</param>
public sealed record CasFlagsResult(int ItemsChanged, int PartsChanged, IReadOnlyList<(string FileName, string Error)> Failures, int SkippedParts = 0, bool StoppedByGame = false);

public class CasFlagsService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(CasFlagsService));

    public static readonly IReadOnlyList<(string Key, uint Bit)> OutfitBits = new[]
    {
        ("Everyday", CasCategoryFlags.Everyday),
        ("Formal", CasCategoryFlags.Formalwear),
        ("Sleepwear", CasCategoryFlags.Sleepwear),
        ("Swimwear", CasCategoryFlags.Swimwear),
        ("Athletic", CasCategoryFlags.Athletic),
        ("Career", CasCategoryFlags.Career),
        ("Outerwear", CasCategoryFlags.Outerwear),
    };

    public static readonly uint EditableBits = OutfitBits.Aggregate(CasCategoryFlags.ValidForRandom, (mask, b) => mask | b.Bit);

    private readonly AppDbContext _db;
    private readonly PackageInspectorService _inspector;
    private readonly PackageEditService _editor;
    private readonly PackageTypeService _packageTypes;
    private readonly CacheBuilderService _cacheBuilder;

    public CasFlagsService(AppDbContext db, PackageInspectorService inspector, PackageEditService editor, PackageTypeService packageTypes, CacheBuilderService cacheBuilder)
    {
        _db = db;
        _inspector = inspector;
        _editor = editor;
        _packageTypes = packageTypes;
        _cacheBuilder = cacheBuilder;
    }

    public static uint Apply(uint category, uint set, uint clear) => (category | (set & EditableBits)) & ~(clear & EditableBits);

    public async Task<CasFlagSummary> SummarizeAsync(IReadOnlyList<long> itemIds, CancellationToken ct = default)
    {
        var counts = new Dictionary<uint, int>();
        int casItems = 0, parts = 0;
        foreach (var id in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var inspection = await _inspector.InspectAsync(id, ct);
                var cas = inspection.Objects.Where(o => o.Kind == InspectedObjectKind.Cas && !o.CouldNotRead).ToList();
                if (cas.Count == 0) continue;
                casItems++;
                parts += cas.Count;
                foreach (var o in cas)
                {
                    for (int b = 0; b < 32; b++)
                    {
                        uint bit = 1u << b;
                        if ((EditableBits & bit) != 0 && (o.CasCategory & bit) != 0) counts[bit] = counts.GetValueOrDefault(bit) + 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn($"Could not read the CAS parts of item {id}: {ex.Message}");
            }
        }
        return new CasFlagSummary(itemIds.Count, casItems, parts, counts);
    }

    public async Task<CasFlagsResult> ApplyToItemsAsync(IReadOnlyList<long> itemIds, uint set, uint clear, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        Sims3GameProcess.ThrowIfGameRunning("Close The Sims 3 before changing the contents of an item.");

        var failures = new List<(string, string)>();
        int itemsChanged = 0, partsChanged = 0, skippedParts = 0, done = 0;
        foreach (var id in itemIds)
        {
            ct.ThrowIfCancellationRequested();
            string fileName = await _db.MetaEntities.Where(m => m.Id == id).Select(m => m.FileName).FirstOrDefaultAsync(ct) ?? id.ToString();
            try
            {
                List<PartEdits> parts;
                using (var inspection = await _inspector.InspectAsync(id, ct))
                {
                    var cas = inspection.Objects.Where(o => o.Kind == InspectedObjectKind.Cas && !o.CouldNotRead).ToList();
                    // Items without CAS parts are not touched, so only CAS items count as failures.
                    if (cas.Count > 0 && inspection.Warnings.Any(w => w.Key is "look_inside.warning_encrypted" or "look_inside.warning_corrupt" or "look_inside.warning_missing_shard"))
                    {
                        throw new InvalidDataException("The item is encrypted, damaged or incomplete");
                    }
                    var changes = cas.Select(o => (Object: o, NewCategory: Apply(o.CasCategory, set, clear))).Where(c => c.NewCategory != c.Object.CasCategory).ToList();
                    skippedParts += changes.Count(c => c.Object.HasDuplicateKey);
                    parts = BuildEdits(inspection, changes.Where(c => !c.Object.HasDuplicateKey));
                }

                if (parts.Count > 0)
                {
                    await ApplyEditsAsync(id, parts);
                    itemsChanged++;
                    partsChanged += parts.Sum(p => p.Edits.Count);
                }
            }
            catch (GameRunningException)
            {
                // Items before this one are already rewritten: report them instead of throwing them away.
                log.Warn($"The game was started while changing CAS flags; stopped before {fileName}");
                return new CasFlagsResult(itemsChanged, partsChanged, failures, skippedParts, StoppedByGame: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn($"Could not change the CAS flags of {fileName}: {ex.Message}");
                failures.Add((fileName, ex.Message));
            }
            progress?.Report((++done, itemIds.Count));
        }
        return new CasFlagsResult(itemsChanged, partsChanged, failures, skippedParts);
    }

    public static List<PartEdits> BuildEdits(PackageInspection inspection, IEnumerable<(InspectedObject Object, uint NewCategory)> changes)
    {
        var byPart = new Dictionary<int, List<ResourceEdit>>();
        foreach (var (obj, newCategory) in changes)
        {
            if (obj.Kind != InspectedObjectKind.Cas || newCategory == obj.CasCategory) continue;
            // Saving keeps one copy of a key, which would drop the other resource with the same key.
            if (obj.HasDuplicateKey) throw new InvalidOperationException($"CAS part {obj.Resource.Key} shares its key with another resource and cannot be edited");
            byte[] patched = ResourceCASP.WithCategory(inspection.Read(obj.Resource), newCategory)
                ?? throw new InvalidDataException($"CAS part {obj.Resource.Key} could not be read");
            int part = obj.Resource.PartIndex;
            if (!byPart.TryGetValue(part, out var list)) byPart[part] = list = new List<ResourceEdit>();
            list.Add(new ResourceEdit(obj.Resource.Key, patched));
        }
        return byPart
            .Select(p => new PartEdits(p.Key, inspection.Parts.FirstOrDefault(x => x.Index == p.Key)?.MetaEntityId, p.Value))
            .ToList();
    }

    public async Task ApplyEditsAsync(long itemId, IReadOnlyList<PartEdits> parts)
    {
        await _editor.ApplyAsync(itemId, parts);
        await RefreshOutfitMetadataAsync(itemId);
        if (await _db.MetaEntities.Where(m => m.Id == itemId).Select(m => m.IsVaultManaged).FirstOrDefaultAsync())
        {
            _cacheBuilder.OnGameContentChanged();
        }
    }

    private async Task RefreshOutfitMetadataAsync(long itemId)
    {
        try
        {
            var meta = await _db.MetaEntities.FirstOrDefaultAsync(m => m.Id == itemId);
            if (meta == null || meta.IsUserTagged || !File.Exists(meta.CompleteFileName)) return;

            bool isSims3Pack = meta.FileType == "TS3PACK" || meta.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
            PackageTypeInfo typeInfo;
            if (meta.IsVaultManaged)
            {
                var chunks = await _db.VaultChunks.Where(c => c.MetaEntityId == meta.Id).ToListAsync();
                if (chunks.Count == 0 && isSims3Pack)
                {
                    var childIds = await _db.MetaEntities.Where(c => c.ParentMetaEntityId == meta.Id).Select(c => c.Id).ToListAsync();
                    chunks = await _db.VaultChunks.Where(c => childIds.Contains(c.MetaEntityId)).ToListAsync();
                }
                typeInfo = _packageTypes.DetectPackageTypeFromVaultChunks(chunks, meta.CompleteFileName, isSims3Pack, meta.FileName);
            }
            else
            {
                typeInfo = _packageTypes.DetectPackageType(meta.CompleteFileName, isSims3Pack);
            }

            if (meta.CASOutfitCategory != typeInfo.CASOutfitCategory)
            {
                meta.CASOutfitCategory = typeInfo.CASOutfitCategory;
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Could not refresh the outfit categories of item {itemId}: {ex.Message}");
        }
    }
}
