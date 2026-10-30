using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CatalogResource;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils.Logging;

namespace PlumbobForge.Backend.Services;

public class HiderGenerationResult
{
    public bool Success { get; set; }
    public ulong CollectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int ObjectCount { get; set; }
    public List<ulong> ObjectGuids { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class Sims3HiderService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(Sims3HiderService));

    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private readonly Sims3HealthService _healthService;

    public Sims3HiderService(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> optionsMonitor,
        Sims3HealthService healthService)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
        _healthService = healthService;
    }

    public string GetOverridesFolderPath()
    {
        string sims3Path = _healthService.GetSims3FolderPath();
        return Path.Combine(sims3Path, "Mods", "Overrides");
    }

    public string GetHiderPackagePath(ulong collectionId)
    {
        return Path.Combine(GetOverridesFolderPath(), $"coll_0x{collectionId:x16}_hider.package");
    }

    public bool HiderPackageExists(ulong collectionId)
    {
        return File.Exists(GetHiderPackagePath(collectionId));
    }

    public bool DeleteHiderPackage(ulong collectionId)
    {
        try
        {
            string path = GetHiderPackagePath(collectionId);
            if (File.Exists(path))
            {
                File.Delete(path);
                log.Info($"Deleted hider package '{path}'");
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Error deleting hider package for collection 0x{collectionId:x16}: {ex.Message}");
        }
        return false;
    }

    public async Task RefreshHidersForSetsAsync(IEnumerable<long> setIds, CancellationToken cancellationToken = default)
    {
        var changedSetIds = setIds.ToHashSet();
        if (changedSetIds.Count == 0) return;

        var collections = await _db.Collections
            .AsNoTracking()
            .Include(c => c.CollectionSets)
            .Where(c => c.HideFromCatalog)
            .ToListAsync(cancellationToken);

        foreach (var collection in collections)
        {
            var includedSetIds = collection.CollectionSets.Select(cs => cs.SetsEntityId).ToList();
            var allSetIds = await SetHierarchyHelper.GetAllDescendantSetIdsAsync(_db, includedSetIds, cancellationToken);
            if (!allSetIds.Any(changedSetIds.Contains)) continue;

            await GenerateHiderPackageAsync(collection.Id, collection.Name, includedSetIds, cancellationToken);
        }
    }

    public async Task<HiderGenerationResult> GenerateHiderPackageAsync(
        ulong collectionId,
        string collectionName,
        IEnumerable<long> includedSetIds,
        CancellationToken cancellationToken = default)
    {
        var result = new HiderGenerationResult { CollectionId = collectionId };

        string folder = GetOverridesFolderPath();
        if (!Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to create Overrides folder: {ex.Message}";
                return result;
            }
        }

        string targetPath = GetHiderPackagePath(collectionId);
        result.FilePath = targetPath;

        var setIdsList = includedSetIds?.Distinct().ToList() ?? new List<long>();
        var allSelectedSetIds = await SetHierarchyHelper.GetAllDescendantSetIdsAsync(_db, setIdsList, cancellationToken);

        // Query enabled items from selected sets (a disabled mod's objects are not in the game to hide)
        var metaItems = await _db.MetaEntities
            .AsNoTracking()
            .Where(m => m.Enabled && m.SetsEntityId.HasValue && allSelectedSetIds.Contains(m.SetsEntityId.Value))
            .ToListAsync(cancellationToken);

        var modifiedObjds = new Dictionary<TGI_Key, byte[]>();
        var objectGuids = new HashSet<ulong>();

        foreach (var item in metaItems)
        {
            if (cancellationToken.IsCancellationRequested) break;

            string filePath = item.CompleteFileName;
            // A vault item's chunk offsets only make sense inside its own shard, so never fall back to a library file.
            if (item.IsVaultManaged && !File.Exists(filePath)) continue;
            if (!File.Exists(filePath))
            {
                string fallback = Path.Combine(_optionsMonitor.CurrentValue.ManagedPackageFolderPath, item.FileName);
                if (File.Exists(fallback))
                {
                    filePath = fallback;
                }
                else
                {
                    continue;
                }
            }

            try
            {
                if (item.IsVaultManaged)
                {
                    var objdChunks = await _db.VaultChunks
                        .AsNoTracking()
                        .Where(c => c.MetaEntityId == item.Id && c.Type == PackageTypeHelper.TypeOBJD)
                        .ToListAsync(cancellationToken);

                    foreach (var chunk in objdChunks)
                    {
                        byte[] rawBytes = S3ForgeTools.GameFiles.Package.DBPFVaultShard.ReadDecompressedChunk(
                            filePath, chunk.Offset, chunk.ChunkLength, chunk.ResourceLength, chunk.IsCompressed);

                        if (TryModifyObjdToHidden(rawBytes, out byte[] modifiedBytes, out ulong guid))
                        {
                            modifiedObjds[new TGI_Key(chunk.Type, chunk.Group, chunk.Instance)] = modifiedBytes;
                            if (guid != 0) objectGuids.Add(guid);
                        }
                    }
                }
                else if (item.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
                {
                    using var s3p = new Sims3Pack(filePath);
                    foreach (var package in s3p.Packages)
                    {
                        var objdEntries = package.Resources
                            .Where(r => r.Key.Type == PackageTypeHelper.TypeOBJD)
                            .ToList();

                        foreach (var entry in objdEntries)
                        {
                            byte[] rawBytes = entry.Read();
                            if (TryModifyObjdToHidden(rawBytes, out byte[] modifiedBytes, out ulong guid))
                            {
                                modifiedObjds[entry.Key] = modifiedBytes;
                                if (guid != 0) objectGuids.Add(guid);
                            }
                        }
                    }
                }
                else
                {
                    using var pkg = new DBPFPackage(filePath);
                    var objdEntries = pkg.Resources
                        .Where(r => r.Key.Type == PackageTypeHelper.TypeOBJD)
                        .ToList();

                    foreach (var entry in objdEntries)
                    {
                        byte[] rawBytes = entry.Read();
                        if (TryModifyObjdToHidden(rawBytes, out byte[] modifiedBytes, out ulong guid))
                        {
                            modifiedObjds[entry.Key] = modifiedBytes;
                            if (guid != 0) objectGuids.Add(guid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Error extracting OBJD from '{filePath}' for hider: {ex.Message}");
            }
        }

        result.ObjectCount = modifiedObjds.Count;
        result.ObjectGuids = objectGuids.OrderBy(g => g).ToList();

        if (modifiedObjds.Count == 0)
        {
            // Nothing to hide, delete existing hider if any
            DeleteHiderPackage(collectionId);
            result.Success = true;
            return result;
        }

        // Build the hider override package
        string tempFile = targetPath + ".tmp";
        try
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);

            using (var builder = new DBPFPackageBuilder(tempFile, false))
            {
                foreach (var kvp in modifiedObjds)
                {
                    using var ms = new MemoryStream(kvp.Value, writable: false);
                    var entry = new ResourceEntry(ms, kvp.Key);
                    builder.AddResource(entry);
                }
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempFile, targetPath);

            result.Success = true;
            log.Info($"Successfully generated hider override package '{targetPath}' with {modifiedObjds.Count} hidden objects.");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Failed to build hider package: {ex.Message}";
            log.Error($"Failed to write hider package '{targetPath}': {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        return result;
    }

    public static bool TryModifyObjdToHidden(byte[] rawBytes, out byte[] modifiedBytes, out ulong guid)
    {
        modifiedBytes = Array.Empty<byte>();
        guid = 0;

        if (rawBytes == null || rawBytes.Length < 32)
            return false;

        try
        {
            using var inStream = new MemoryStream(rawBytes, writable: false);
            var objd = new ObjectCatalogResource(1, inStream);
            if (objd == null || objd.CommonBlock == null)
                return false;

            guid = objd.CommonBlock.NameGUID != 0 ? objd.CommonBlock.NameGUID : (ulong)objd.CommonBlock.ProductNameHash;

            // Set status flags to 0x60 (ProductionProduct 0x20 | ObjProductMadeUsingNewEntryScheme 0x40, without ShowInCatalog 0x1)
            objd.CommonBlock.BuildBuyProductStatusFlags = (CatalogResource.CatalogResource.Common.BuildBuyProductStatus)0x60;

            using var outStream = objd.Stream;
            using var ms = new MemoryStream();
            outStream.CopyTo(ms);
            modifiedBytes = ms.ToArray();

            return modifiedBytes.Length > 0;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to parse/modify OBJD with s3pi: {ex.Message}");
            return false;
        }
    }

    public string GenerateHiderXml(string collectionName, IEnumerable<ulong> guids)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("RecategorizationTable",
                new XAttribute("Generator", "PlumbobForge (Batch Resource Editor Compatible)"),
                new XElement("Recategorization",
                    guids.Select(g => new XElement("GUID", $"0x{g:X16}")),
                    new XElement("OBJD",
                        new XElement("BuildBuyProductStatus", "0x60")
                    )
                )
            )
        );

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        {
            doc.Save(writer);
        }
        return sb.ToString();
    }
}
