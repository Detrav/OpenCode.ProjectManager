using Microsoft.Data.Sqlite;
using OpenCode.ProjectManager.Core.Services;
using Xunit;

namespace OpenCode.ProjectManager.Tests;

public sealed class DatabaseMaintenanceTests
{
    [Fact]
    public void ReadSpaceInfo_ReportsPageUsage()
    {
        using var database = new TestDatabase();
        database.Create();

        var info = new DatabaseMaintenanceService().ReadSpaceInfo(database.DatabasePath);

        Assert.True(info.FileSizeBytes > 0);
        Assert.True(info.PageSize > 0);
        Assert.True(info.PageCount > 0);
        Assert.True(info.LiveBytes > 0);
        Assert.True(info.ReclaimableBytes >= 0);
    }

    [Fact]
    public void Compact_ReclaimsFreePagesAndKeepsData()
    {
        using var database = new TestDatabase();
        database.Create();
        CreateFreePages(database.DatabasePath);

        var service = new DatabaseMaintenanceService();
        var before = service.ReadSpaceInfo(database.DatabasePath);
        Assert.True(before.ReclaimableBytes > 0);

        var after = service.Compact(database.DatabasePath);

        Assert.True(after.FileSizeBytes < before.FileSizeBytes);
        Assert.Equal(0, after.ReclaimableBytes);
        Assert.Equal(4L, database.Scalar("SELECT COUNT(*) FROM project;"));
    }

    private static void CreateFreePages(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO blob (hash, mime, bytes, data, time_created)
            VALUES ('ballast', 'application/octet-stream', 8388608, randomblob(8388608), 1);

            DELETE FROM blob WHERE hash = 'ballast';
            """;
        command.ExecuteNonQuery();
    }
}
