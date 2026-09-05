using StarlightExporter.Persistence;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class PrivateAccountValidatorTests
{
    [Fact]
    public async Task ExistingAccountIsAcceptedThroughStarlightSdkModel()
    {
        string testDirectory = CreateTestDirectory();
        string databasePath = Path.Combine(testDirectory, "accounts.db");

        try
        {
            await TestAccountDatabase.CreateAsync(databasePath, 7);

            PrivateAccountValidationResult result =
                await PrivateAccountValidator.ValidateExistsAsync(databasePath, "7");

            Assert.True(result.IsValid);
            Assert.Equal("ACCOUNT_FOUND", result.Code);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAccountIsReportedWithoutChangingDatabase()
    {
        string testDirectory = CreateTestDirectory();
        string databasePath = Path.Combine(testDirectory, "accounts.db");

        try
        {
            await TestAccountDatabase.CreateAsync(databasePath, 7);
            long originalLength = new FileInfo(databasePath).Length;

            PrivateAccountValidationResult result =
                await PrivateAccountValidator.ValidateExistsAsync(databasePath, "8");

            Assert.False(result.IsValid);
            Assert.Equal("ACCOUNT_NOT_FOUND", result.Code);
            Assert.Equal(originalLength, new FileInfo(databasePath).Length);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-a-number")]
    [InlineData("4294967296")]
    public async Task InvalidSdkAccountIdIsRejected(string accountId)
    {
        PrivateAccountValidationResult result = await PrivateAccountValidator.ValidateExistsAsync(
            Path.Combine(Path.GetTempPath(), "not-opened.db"),
            accountId);

        Assert.False(result.IsValid);
        Assert.Equal("PRIVATE_ACCOUNT_ID_INVALID", result.Code);
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
