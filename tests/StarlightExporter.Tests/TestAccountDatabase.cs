using Microsoft.EntityFrameworkCore;
using Starlight.SDK.Database;
using Starlight.SDK.Database.Models;

namespace StarlightExporter.Tests;

internal static class TestAccountDatabase
{
    public static async Task CreateAsync(string path, params uint[] accountIds)
    {
        var options = new DbContextOptionsBuilder<SdkDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;

        await using var database = new SdkDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.Accounts.AddRange(accountIds.Select(id => new Account {
            Id = id,
            Username = $"test-{id}"
        }));
        await database.SaveChangesAsync();
    }
}
