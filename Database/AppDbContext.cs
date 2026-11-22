using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace PlumbobForge.Backend.Database;

public class AppDbContext : DbContext
{
    public DbSet<MetaEntity> MetaEntities { get; set; }
    public DbSet<SetsEntity> SetsEntities { get; set; }
    public DbSet<ConfigEntity> ConfigEntities { get; set; }
    public DbSet<ConfigSetsEntity> ConfigSetsEntities { get; set; }
    public DbSet<SettingEntity> SettingEntities { get; set; }
    public DbSet<TombstoneEntity> Tombstones { get; set; }
    public DbSet<CollectionEntity> Collections { get; set; }
    public DbSet<CollectionSetsEntity> CollectionSets { get; set; }
    public DbSet<CollectionItemsEntity> CollectionItems { get; set; }
    public DbSet<VaultShardEntity> VaultShards { get; set; }
    public DbSet<VaultChunkEntity> VaultChunks { get; set; }
    public DbSet<VaultHoleEntity> VaultHoles { get; set; }
    public DbSet<DisabledObjectEntity> DisabledObjects { get; set; }
    public DbSet<ConfigItemOverrideEntity> ConfigItemOverrides { get; set; }
    public DbSet<ModsFolderEntity> ModsFolders { get; set; }
    public DbSet<ConfigModsFolderOffEntity> ConfigModsFoldersOff { get; set; }

    // Every item including Mods folder files, which MetaEntities leaves out. With Mods folder support off this is
    // the same as MetaEntities, so tools stop showing Mods items.
    public IQueryable<MetaEntity> AllMetaEntities => PlumbobForge.Backend.Configuration.ModsFolderSupport.IsEnabled
        ? MetaEntities.IgnoreQueryFilters() : MetaEntities;

    // Mods items whether or not support is on: Mods folder housekeeping and cleanup that must not mistake them for orphans.
    public IQueryable<MetaEntity> EveryMetaEntity => MetaEntities.IgnoreQueryFilters();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SetsEntity>()
            .HasOne(s => s.ParentSetsEntity)
            .WithMany(s => s.Children)
            .HasForeignKey(s => s.ParentSetsEntityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MetaEntity>()
            .HasOne(m => m.SetsEntity)
            .WithMany(s => s.MetaEntities)
            .HasForeignKey(m => m.SetsEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MetaEntity>()
            .Property(m => m.FileName)
            .UseCollation("NOCASE");

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.SetsEntityId);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.Enabled);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.IsFavorite);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.PackageType);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.FileName);

        modelBuilder.Entity<TombstoneEntity>()
            .Property(t => t.FileName)
            .UseCollation("NOCASE");

        modelBuilder.Entity<ConfigSetsEntity>()
            .HasOne(c => c.ConfigEntity)
            .WithMany(ce => ce.ConfigSetsEntities)
            .HasForeignKey(c => c.ConfigEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConfigSetsEntity>()
            .HasOne(c => c.SetsEntity)
            .WithMany()
            .HasForeignKey(c => c.SetsEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CollectionSetsEntity>()
            .HasOne(cs => cs.Collection)
            .WithMany(c => c.CollectionSets)
            .HasForeignKey(cs => cs.CollectionEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CollectionSetsEntity>()
            .HasOne(cs => cs.Set)
            .WithMany()
            .HasForeignKey(cs => cs.SetsEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CollectionItemsEntity>()
            .HasOne(ci => ci.Collection)
            .WithMany(c => c.CollectionItems)
            .HasForeignKey(ci => ci.CollectionEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CollectionItemsEntity>()
            .HasOne(ci => ci.Item)
            .WithMany()
            .HasForeignKey(ci => ci.MetaEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SettingEntity>()
            .HasOne(s => s.ConfigEntity)
            .WithMany(c => c.SettingEntities)
            .HasForeignKey(s => s.ConfigEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultShardEntity>()
            .HasOne(vs => vs.SetsEntity)
            .WithMany(s => s.VaultShards)
            .HasForeignKey(vs => vs.SetsEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultShardEntity>()
            .HasIndex(vs => new { vs.SetsEntityId, vs.ShardIndex })
            .IsUnique();

        modelBuilder.Entity<VaultChunkEntity>()
            .HasOne(vc => vc.MetaEntity)
            .WithMany(m => m.VaultChunks)
            .HasForeignKey(vc => vc.MetaEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultChunkEntity>()
            .HasOne(vc => vc.VaultShard)
            .WithMany(vs => vs.Chunks)
            .HasForeignKey(vc => vc.VaultShardId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultChunkEntity>()
            .HasIndex(vc => new { vc.Type, vc.Group, vc.Instance });

        modelBuilder.Entity<VaultChunkEntity>()
            .HasIndex(vc => vc.MetaEntityId);

        modelBuilder.Entity<VaultChunkEntity>()
            .HasIndex(vc => vc.VaultShardId);

        modelBuilder.Entity<VaultHoleEntity>()
            .HasOne(vh => vh.VaultShard)
            .WithMany(vs => vs.Holes)
            .HasForeignKey(vh => vh.VaultShardId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VaultHoleEntity>()
            .HasIndex(vh => vh.VaultShardId);

        modelBuilder.Entity<MetaEntity>()
            .HasOne(m => m.ParentMetaEntity)
            .WithMany(p => p.SubItems)
            .HasForeignKey(m => m.ParentMetaEntityId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetaEntity>()
            .HasOne(m => m.VaultShard)
            .WithMany()
            .HasForeignKey(m => m.VaultShardId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.VaultShardId);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.ParentMetaEntityId);

        modelBuilder.Entity<DisabledObjectEntity>()
            .HasOne(d => d.MetaEntity)
            .WithMany()
            .HasForeignKey(d => d.MetaEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DisabledObjectEntity>()
            .HasIndex(d => d.MetaEntityId);

        modelBuilder.Entity<ConfigItemOverrideEntity>()
            .HasOne(o => o.ConfigEntity)
            .WithMany(c => c.ItemOverrides)
            .HasForeignKey(o => o.ConfigEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConfigItemOverrideEntity>()
            .HasOne(o => o.MetaEntity)
            .WithMany()
            .HasForeignKey(o => o.MetaEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConfigItemOverrideEntity>()
            .HasIndex(o => new { o.ConfigEntityId, o.MetaEntityId })
            .IsUnique();

        modelBuilder.Entity<ConfigItemOverrideEntity>()
            .HasIndex(o => o.MetaEntityId);

        // Files in the game's Mods folder are managed in place. Library and vault code was written for items it owns;
        // hiding Mods items from every query by default keeps it from renaming, deduplicating or rebuilding them.
        // Code that handles Mods items reads AllMetaEntities.
        modelBuilder.Entity<MetaEntity>()
            .HasQueryFilter(m => !m.IsModsItem);

        modelBuilder.Entity<MetaEntity>()
            .HasOne(m => m.ModsFolder)
            .WithMany(f => f.MetaEntities)
            .HasForeignKey(m => m.ModsFolderId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetaEntity>()
            .HasIndex(m => m.ModsFolderId);

        modelBuilder.Entity<ModsFolderEntity>()
            .HasOne(f => f.ParentModsFolder)
            .WithMany(f => f.Children)
            .HasForeignKey(f => f.ParentModsFolderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ModsFolderEntity>()
            .Property(f => f.RelativePath)
            .UseCollation("NOCASE");

        modelBuilder.Entity<ModsFolderEntity>()
            .HasIndex(f => f.RelativePath)
            .IsUnique();

        modelBuilder.Entity<ConfigModsFolderOffEntity>()
            .HasOne(o => o.ConfigEntity)
            .WithMany(c => c.ModsFoldersOff)
            .HasForeignKey(o => o.ConfigEntityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConfigModsFolderOffEntity>()
            .HasOne(o => o.ModsFolder)
            .WithMany()
            .HasForeignKey(o => o.ModsFolderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConfigModsFolderOffEntity>()
            .HasIndex(o => new { o.ConfigEntityId, o.ModsFolderId })
            .IsUnique();

        modelBuilder.Entity<ConfigModsFolderOffEntity>()
            .HasIndex(o => o.ModsFolderId);
    }
}
