using System;
using System.Collections.Generic;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public class CollectionModel
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public TGI_Key IconKey { get; set; } = new(0x2F7D0004, 0, 0);
    public string IconName { get; set; } = "Default";
    public string IconCategory { get; set; } = "General";
    public uint Flags { get; set; } = 0;
    public int ItemCount { get; set; }
    public List<long> LinkedSetIds { get; set; } = new();
    public List<string> LinkedSetNames { get; set; } = new();
    public Dictionary<TGI_Key, uint> Items { get; set; } = new();
    public byte[]? IconBytes { get; set; }
    public bool IsPlumbobForge { get; set; } = false;
    public bool HideFromCatalog { get; set; } = false;
    public DateTime? LastModified { get; set; }
}

public class CollectionIconDef
{
    public string KeyName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public TGI_Key Key { get; set; } = new(0x2F7D0004, 0, 0);
    public string BoxIconKind { get; set; } = "RegularFolder";
    public byte[]? IconBytes { get; set; }
}

public class CollectionSetOption
{
    public long SetId { get; set; }
    public string SetName { get; set; } = string.Empty;
    public string BreadcrumbPath { get; set; } = string.Empty;
    public string Icon { get; set; } = "RegularFolder";
    public string? Color { get; set; }
    public int ItemCount { get; set; }
    public bool IsSelected { get; set; }
}

public class CollectionSaveResult
{
    public bool Success { get; set; }
    public ulong CollectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public string? ErrorMessage { get; set; }
}
