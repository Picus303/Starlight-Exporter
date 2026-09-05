using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Starlight.SDK.Database;

namespace StarlightExporter.Persistence;

public sealed record PrivateAccountValidationResult(
    bool IsValid,
    string Code,
    string Message);

public static class PrivateAccountValidator
{
    public static async Task<PrivateAccountValidationResult> ValidateExistsAsync(
        string databasePath,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (!uint.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsedAccountId)
            || parsedAccountId == 0)
        {
            return Invalid(
                "PRIVATE_ACCOUNT_ID_INVALID",
                "The private account ID must be a non-zero UInt32 value when accounts.db validation is enabled.");
        }

        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            return Invalid("ACCOUNT_DATABASE_NOT_FOUND", $"Account database not found: '{fullPath}'.");
        }

        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<SdkDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using var database = new SdkDbContext(options);
            bool exists = await database.Accounts
                .AsNoTracking()
                .AnyAsync(account => account.Id == parsedAccountId, cancellationToken);

            return exists
                ? new PrivateAccountValidationResult(
                    IsValid: true,
                    Code: "ACCOUNT_FOUND",
                    Message: $"Private account {accountId} exists in the supplied account database.")
                : Invalid(
                    "ACCOUNT_NOT_FOUND",
                    $"Private account {accountId} does not exist in the supplied account database.");
        }
        catch (Exception exception) when (exception is SqliteException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return Invalid(
                "ACCOUNT_DATABASE_INVALID",
                "The account database could not be read with the pinned Starlight SDK schema.");
        }
    }

    private static PrivateAccountValidationResult Invalid(string code, string message) =>
        new(IsValid: false, code, message);
}
