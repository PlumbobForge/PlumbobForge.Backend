using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public class PackageTypeService
{
    private readonly AppDbContext _db;
    private readonly LocalizationService _localizer;

    public PackageTypeService(AppDbContext db, LocalizationService localizer)
    {
        _db = db;
        _localizer = localizer;
    }

    public (string PackageType, string CASCategories, string CASAge, string CASGender, string CASOutfitCategory) DetectPackageType(string filePath, bool isSims3Pack)
    {
        try
        {
            var packages = new List<S3ForgeTools.GameFiles.Package.DBPFPackage>();
            S3ForgeTools.GameFiles.TS3Pack.Sims3Pack? s3p = null;

            bool isWorld = filePath.EndsWith(".world", StringComparison.OrdinalIgnoreCase);
            bool isSim = filePath.EndsWith(".sim", StringComparison.OrdinalIgnoreCase);
            bool isLot = false;

            if (isSims3Pack)
            {
                s3p = new S3ForgeTools.GameFiles.TS3Pack.Sims3Pack(filePath);
                packages.AddRange(s3p.Packages);

                if (!string.IsNullOrEmpty(s3p.Type))
                {
                    if (s3p.Type.Equals("World", StringComparison.OrdinalIgnoreCase) ||
                        (s3p.SubType != null && s3p.SubType.Equals("World", StringComparison.OrdinalIgnoreCase)))
                    {
                        isWorld = true;
                    }
                    else if (s3p.Type.Equals("Lot", StringComparison.OrdinalIgnoreCase) ||
                             s3p.Type.Equals("House", StringComparison.OrdinalIgnoreCase) ||
                             (s3p.SubType != null && s3p.SubType.Equals("Lot", StringComparison.OrdinalIgnoreCase)))
                    {
                        isLot = true;
                    }
                    else if (s3p.Type.Equals("Sim", StringComparison.OrdinalIgnoreCase) ||
                             s3p.Type.Equals("Household", StringComparison.OrdinalIgnoreCase) ||
                             (s3p.SubType != null && s3p.SubType.Equals("Sim", StringComparison.OrdinalIgnoreCase)))
                    {
                        isSim = true;
                    }
                }
            }
            else
            {
                packages.Add(new S3ForgeTools.GameFiles.Package.DBPFPackage(filePath));
            }

            if (isWorld)
            {
                s3p?.Dispose();
                foreach (var p in packages) if (!isSims3Pack) p.Dispose();
                return ("World", "", "", "", "");
            }
            if (isLot)
            {
                s3p?.Dispose();
                foreach (var p in packages) if (!isSims3Pack) p.Dispose();
                return ("Lot", "", "", "", "");
            }
            if (isSim)
            {
                s3p?.Dispose();
                foreach (var p in packages) if (!isSims3Pack) p.Dispose();
                return ("Sim", "", "", "", "");
            }

            var categories = new HashSet<string>();
            var ages = new HashSet<string>();
            var genders = new HashSet<string>();
            var outfitCategories = new HashSet<string>();

            bool isBuildBuy = false;
            bool hasCaspOverall = false;
            bool hasWorldRes = false;
            bool hasLotRes = false;
            bool hasSimRes = false;
            bool hasPatternRes = false;

            foreach (var package in packages)
            {
                var caspList = new List<S3ForgeTools.GameFiles.Package.ResourceEntry>();
                bool hasBuildBuyThisPackage = false;
                bool hasFaceModifier = false;
                bool hasBlendGeom = false;
                bool hasTone = false;
                bool hasPreset1 = false;

                foreach (var res in package.Resources)
                {
                    uint type = res.Key.Type;
                    switch (type)
                    {
                        case 107542056: // World
                            hasWorldRes = true;
                            break;
                        case 3496170587u: // Lot
                            hasLotRes = true;
                            break;
                        case 83396964: // Sim
                            hasSimRes = true;
                            break;
                        case 0xD4D9FBE5: // Pattern
                            hasPatternRes = true;
                            break;
                        case 0x034AEECB: // CASP
                            caspList.Add(res);
                            break;
                        case 0x319E4F1D: // OBJD
                            hasBuildBuyThisPackage = true;
                            break;
                        case 0x0358B08A: // FaceModifier
                            hasFaceModifier = true;
                            break;
                        case 0x0355E0A6: // BlendGeom
                            hasBlendGeom = true;
                            break;
                        case 0x0166038C: // Tone
                            hasTone = true;
                            break;
                        case 0x051DF2DD: // Preset
                            hasPreset1 = true;
                            break;
                    }
                }

                if (caspList.Count > 0)
                {
                    hasCaspOverall = true;
                    foreach (var caspEntry in caspList)
                    {
                        try
                        {
                            var data = caspEntry.Read();
                            var casp = new S3ForgeTools.GameFiles.Resources.ResourceCASP(data);
                            switch (casp.ClothingType)
                            {
                                case 0x1: categories.Add("Hair"); break;
                                case 0x4: categories.Add("Full body"); break;
                                case 0x5: categories.Add("Tops"); break;
                                case 0x6: categories.Add("Bottoms"); break;
                                case 0x7: categories.Add("Shoes"); break;
                                case 0x8: case 0x9: case 0xA: case 0xB: case 0xC: case 0xD: case 0xE: case 0xF:
                                    categories.Add("Accessories"); break;
                                case 0x10: case 0x11: case 0x12: case 0x13: case 0x14: case 0x15: case 0x16: case 0x17:
                                    categories.Add("Details"); break;
                                default: categories.Add("Other"); break;
                            }

                            if ((casp.AgeGender & 0x0001) != 0) ages.Add("Baby");
                            if ((casp.AgeGender & 0x0002) != 0) ages.Add("Toddler");
                            if ((casp.AgeGender & 0x0004) != 0) ages.Add("Child");
                            if ((casp.AgeGender & 0x0008) != 0) ages.Add("Teen");
                            if ((casp.AgeGender & 0x0010) != 0) ages.Add("YoungAdult");
                            if ((casp.AgeGender & 0x0020) != 0) ages.Add("Adult");
                            if ((casp.AgeGender & 0x0040) != 0) ages.Add("Elder");

                            if ((casp.AgeGender & 0x1000) != 0) genders.Add("Male");
                            if ((casp.AgeGender & 0x2000) != 0) genders.Add("Female");

                            if ((casp.Category & 0x0001) != 0) outfitCategories.Add("Everyday");
                            if ((casp.Category & 0x0002) != 0) outfitCategories.Add("Formal");
                            if ((casp.Category & 0x0004) != 0) outfitCategories.Add("Sleepwear");
                            if ((casp.Category & 0x0008) != 0) outfitCategories.Add("Swimwear");
                            if ((casp.Category & 0x0010) != 0) outfitCategories.Add("Athletic");
                            if ((casp.Category & 0x0020) != 0) outfitCategories.Add("Career");
                            if ((casp.Category & 0x0040) != 0) outfitCategories.Add("Outerwear");
                        }
                        catch { }
                    }
                }
                else
                {
                    if (hasBuildBuyThisPackage)
                    {
                        isBuildBuy = true;
                    }
                    else
                    {
                        if (hasFaceModifier || hasBlendGeom) categories.Add("Sliders");
                        else if (hasTone) categories.Add("Skins");
                        else if (hasPreset1) categories.Add("Presets");
                    }
                }
            }

            s3p?.Dispose();

            foreach (var package in packages)
            {
                if (!isSims3Pack) package.Dispose();
            }

            if (hasWorldRes) return ("World", "", "", "", "");
            if (hasLotRes) return ("Lot", "", "", "", "");

            if (hasCaspOverall)
            {
                categories.Remove("Sliders");
                categories.Remove("Presets");
                categories.Remove("Skins");
                string joinedCat = string.Join(",", categories);
                string joinedAge = string.Join(",", ages);
                string joinedGender = string.Join(",", genders);
                string joinedOutfit = string.Join(",", outfitCategories);
                return ("CAS", joinedCat, joinedAge, joinedGender, joinedOutfit);
            }

            if (isBuildBuy) return ("BuildBuy", "", "", "", "");
            if (hasPatternRes) return ("Pattern", "", "", "", "");
            if (hasSimRes) return ("Sim", "", "", "", "");

            if (categories.Any())
            {
                string joinedCat = string.Join(",", categories);
                return ("CAS", joinedCat, "", "", "");
            }
        }
        catch { /* ignore parsing errors */ }

        return ("Other", "", "", "", "");
    }

    public async Task<(int TotalScanned, int UpdatedCount)> RecheckPackageTypesAsync(Action<string>? onProgress = null, bool skipUserTagged = true, ITaskProgressReporter? progress = null)
    {
        var items = await _db.MetaEntities.ToListAsync();
        int count = 0;
        int updatedCount = 0;
        int total = items.Count;

        string scanStep = "scan_types";
        progress?.StartStep(scanStep, _localizer.GetString("progress.scanning_packages_title"), badge: $"0/{total}", progress: 0.0);

        int interval = Math.Max(1, total / 50);

        foreach (var item in items)
        {
            count++;

            if (!skipUserTagged || !item.IsUserTagged)
            {
                if (File.Exists(item.CompleteFileName))
                {
                    bool isSims3Pack = item.FileType == "TS3PACK" || item.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
                    var typeInfo = DetectPackageType(item.CompleteFileName, isSims3Pack);

                    if (item.PackageType != typeInfo.PackageType ||
                        item.CASCategories != typeInfo.CASCategories ||
                        item.CASAge != typeInfo.CASAge ||
                        item.CASGender != typeInfo.CASGender ||
                        item.CASOutfitCategory != typeInfo.CASOutfitCategory)
                    {
                        item.PackageType = typeInfo.PackageType;
                        item.CASCategories = typeInfo.CASCategories;
                        item.CASAge = typeInfo.CASAge;
                        item.CASGender = typeInfo.CASGender;
                        item.CASOutfitCategory = typeInfo.CASOutfitCategory;
                        updatedCount++;
                    }
                }
            }

            if (count % interval == 0 || count == total)
            {
                double pct = total > 0 ? (double)count / total : 1.0;
                progress?.UpdateStep(scanStep, progress: pct, badge: $"{count}/{total}");
                onProgress?.Invoke(_localizer.GetString("rechecking_package_types", count, total));
            }
        }

        if (updatedCount > 0)
        {
            await _db.SaveChangesAsync();
        }

        // Complete the scanning step
        progress?.CompleteStep(scanStep, finalBadge: $"{total}/{total}");

        // Add the summary result bar once finished, with count in the title text and no badge
        string resultStep = "result_types";
        progress?.StartStep(resultStep, _localizer.GetString("progress.category_changed_badge", updatedCount), badge: "", progress: 1.0);
        progress?.CompleteStep(resultStep, finalBadge: "");

        onProgress?.Invoke(_localizer.GetString("package_type_check_complete", updatedCount));

        return (total, updatedCount);
    }
}
