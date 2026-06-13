using Microsoft.EntityFrameworkCore;
using MyScorer.Core.Models;

namespace MyScorer.Application.Data;

public class MyScorerDbContext : DbContext
{
    public MyScorerDbContext(DbContextOptions<MyScorerDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Call once at app startup to create new tables in existing databases.
    /// </summary>
    public void EnsureNewTablesCreated()
    {
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "StreamingDevices" (
                "DeviceId" TEXT NOT NULL PRIMARY KEY,
                "Name" TEXT NOT NULL DEFAULT '',
                "DeviceType" TEXT NOT NULL DEFAULT 'raspberry-pi',
                "IsOnline" INTEGER NOT NULL DEFAULT 0,
                "LastSeen" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
                "AtemConnected" INTEGER NOT NULL DEFAULT 0,
                "StreamActive" INTEGER NOT NULL DEFAULT 0,
                "NetworkStatus" TEXT NOT NULL DEFAULT 'unknown'
            );
            """);

        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "DeviceCommands" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DeviceId" TEXT NOT NULL DEFAULT '',
                "Command" TEXT NOT NULL DEFAULT '',
                "RequestId" TEXT NOT NULL DEFAULT '',
                "Status" TEXT NOT NULL DEFAULT 'Pending',
                "CreatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'
            );
            """);

        // Indexes for command queries (next-command, idempotency, purge)
        Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_DeviceCommands_DeviceId_Status"
            ON "DeviceCommands" ("DeviceId", "Status");
            """);

        Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_DeviceCommands_CreatedAt"
            ON "DeviceCommands" ("CreatedAt");
            """);
    }

    public DbSet<SetupRecord> Setups => Set<SetupRecord>();
    public DbSet<ClientRecord> Clients => Set<ClientRecord>();
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    public DbSet<StreamingDevice> StreamingDevices => Set<StreamingDevice>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SetupRecord>().HasKey(x => x.SetupId);
        modelBuilder.Entity<ClientRecord>().HasKey(x => x.SetupId);
        modelBuilder.Entity<MatchRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<StreamingDevice>().HasKey(x => x.DeviceId);
        modelBuilder.Entity<DeviceCommand>().HasKey(x => x.Id);

        modelBuilder.Entity<MatchRecord>()
            .Property(x => x.Id)
            .ValueGeneratedOnAdd();

        modelBuilder.Entity<DeviceCommand>()
            .Property(x => x.Id)
            .ValueGeneratedOnAdd();
    }
}
