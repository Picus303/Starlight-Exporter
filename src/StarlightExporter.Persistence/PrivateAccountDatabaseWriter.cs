using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Starlight.Crypto;
using Starlight.SDK;
using Starlight.SDK.Database;
using Starlight.SDK.Database.Models;

namespace StarlightExporter.Persistence;

public sealed record PrivateAccountWriteResult(
    string OutputPath,
    uint AccountId,
    string Username);

public static class PrivateAccountDatabaseWriter
{
    public static async Task<PrivateAccountWriteResult> WriteNewAsync(
        string path,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (username.Length > Account.MaxUsernameLength)
        {
            throw new ArgumentException(
                $"The private username cannot exceed {Account.MaxUsernameLength} characters.",
                nameof(username));
        }

        var sdkDefaults = new SdkConfig();
        int maximumPasswordLength = sdkDefaults.MaPassport.Login.MaxPasswordLength;
        if (password.Length < sdkDefaults.MinPasswordLength || password.Length > maximumPasswordLength)
        {
            throw new ArgumentException(
                $"The private password must contain {sdkDefaults.MinPasswordLength}-{maximumPasswordLength} characters.",
                nameof(password));
        }

        string outputPath = Path.GetFullPath(path);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException("The private account database output already exists.");
        }

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var connectionString = new SqliteConnectionStringBuilder {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            var options = new DbContextOptionsBuilder<SdkDbContext>()
                .UseSqlite(connectionString)
                .Options;

            uint accountId;
            await using (var database = new SdkDbContext(options))
            {
                await database.Database.EnsureCreatedAsync(cancellationToken);
                var account = new Account {
                    Username = username,
                    PasswordHash = Argon2Crypto.Hash(password),
                    PasswordTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                database.Accounts.Add(account);
                await database.SaveChangesAsync(cancellationToken);
                accountId = account.Id;
            }

            SqliteConnection.ClearAllPools();
            await VerifyAsync(temporaryPath, accountId, username, cancellationToken);
            SqliteConnection.ClearAllPools();
            DeleteIfPresent(temporaryPath + "-wal");
            DeleteIfPresent(temporaryPath + "-shm");
            File.Move(temporaryPath, outputPath);
            return new PrivateAccountWriteResult(outputPath, accountId, username);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteIfPresent(temporaryPath);
            DeleteIfPresent(temporaryPath + "-wal");
            DeleteIfPresent(temporaryPath + "-shm");
            throw;
        }
    }

    private static async Task VerifyAsync(
        string path,
        uint accountId,
        string username,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var options = new DbContextOptionsBuilder<SdkDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using var database = new SdkDbContext(options);
        Account account = await database.Accounts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (!string.Equals(account.Username, username, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(account.PasswordHash))
        {
            throw new InvalidDataException("The private account database failed verification.");
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
