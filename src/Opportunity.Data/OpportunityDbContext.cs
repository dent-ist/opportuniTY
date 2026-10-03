using System.Text;

using Microsoft.EntityFrameworkCore;

using Opportunity.Core.Documents;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Documents;

namespace Opportunity.Data;

/// <summary>
/// EF Core mapping of the core schema. The schema itself is owned by the SQL migrations (never EF migrations).
/// Documents and their projection state are read-only here: their writes go through <see cref="DocumentRepository"/>,
/// which maintains DocumentVersion (ADR-001 §2).
/// </summary>
public sealed class OpportunityDbContext(DbContextOptions<OpportunityDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentProjectionState> DocumentProjectionStates => Set<DocumentProjectionState>();

    public DbSet<StoredObject> StoredObjects => Set<StoredObject>();

    public DbSet<PageSet> PageSets => Set<PageSet>();

    public DbSet<Page> Pages => Set<Page>();

    public DbSet<PageImage> PageImages => Set<PageImage>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectDocumentWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectDocumentWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("opportunity");

        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.WorkspaceId);
            e.Property(w => w.Status).HasConversion<string>();
            e.Property(w => w.CreatedAt).HasDefaultValueSql("now()");
            e.Property(w => w.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(d => new { d.WorkspaceId, d.DocumentId });
            e.Property(d => d.ControlNumberSortKey).ValueGeneratedOnAddOrUpdate();
            e.Property(d => d.Metadata).HasColumnType("jsonb");
            e.Property(d => d.MetadataRaw).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DocumentProjectionState>(e => e.HasKey(s => new { s.WorkspaceId, s.DocumentId }));
        modelBuilder.Entity<StoredObject>(e =>
        {
            e.HasKey(o => new { o.WorkspaceId, o.ObjectId });
            e.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
        });
        modelBuilder.Entity<PageSet>(e =>
        {
            e.HasKey(p => new { p.WorkspaceId, p.PageSetId });
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });
        modelBuilder.Entity<Page>(e =>
        {
            e.HasKey(p => new { p.WorkspaceId, p.PageSetId, p.Ordinal });
            e.Property(p => p.WidthPt).HasPrecision(8, 2);
            e.Property(p => p.HeightPt).HasPrecision(8, 2);
        });
        modelBuilder.Entity<PageImage>(e => e.HasKey(p => new { p.WorkspaceId, p.PageSetId, p.Ordinal, p.Purpose }));

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.ClrType.Name));
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private void RejectDocumentWrites()
    {
        if (ChangeTracker.Entries().Any(e =>
                e.Entity is Document or DocumentProjectionState
                && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Documents are written through IDocumentRepository so that DocumentVersion is maintained (ADR-001).");
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && !char.IsUpper(name[i - 1]))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
