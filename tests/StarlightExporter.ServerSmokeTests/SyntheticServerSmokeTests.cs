using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Starlight.DbGate;
using Starlight.DbGate.Models;
using Starlight.Rpc.Proto;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;
using Xunit;

namespace StarlightExporter.Tests;

[Collection("Real resources")]
public sealed class SyntheticServerSmokeTests
{
    private const string SyntheticUsername = "starlight-smoke";
    private const string SyntheticPassword = "synthetic-test-only";

    [ServerSmokeFact]
    public async Task PinnedServerStartsAndPreservesSyntheticExport()
    {
        string repositoryRoot = RealResourceArchive.FindRepositoryRoot()!;
        string resourcesPath = RealResourceArchive.Find()!;
        string serverPath = ServerSmokeFactAttribute.FindServer(repositoryRoot)!;
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.ServerSmoke",
            Guid.NewGuid().ToString("N"));
        string runtimeDirectory = Path.Combine(testDirectory, "runtime");
        string accountDatabasePath = Path.Combine(testDirectory, "accounts.db");
        string playerDatabasePath = Path.Combine(testDirectory, "starlight.db");
        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            await TestAccountDatabase.CreateLoginAccountAsync(
                accountDatabasePath,
                accountId: 42,
                SyntheticUsername,
                SyntheticPassword);
            OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "smoke-real-resources.json"));
            StarlightTargetPreflightResult preflight = await StarlightTargetPreflight.RunAsync(
                snapshot,
                resourcesPath);
            StarlightMappingResult mapping = preflight.Mapping;
            Assert.True(mapping.IsSuccess, string.Join(Environment.NewLine, mapping.Issues));
            StarlightModuleValidationResult moduleValidation = Assert.IsType<StarlightModuleValidationResult>(
                preflight.ModuleValidation);
            Assert.True(moduleValidation.IsCompatible, string.Join(Environment.NewLine, moduleValidation.Diagnostics));

            await StarlightDatabaseWriter.WriteNewAsync(new StarlightDatabaseWriteRequest(
                playerDatabasePath,
                snapshot.Manifest.OfficialUid,
                PrivateAccountId: "42",
                mapping.Profile,
                mapping.State));

            int sdkPort = ReserveTcpPort();
            int gatePort = ReserveUdpPort();
            using Process process = StartServer(
                serverPath,
                runtimeDirectory,
                resourcesPath,
                playerDatabasePath,
                accountDatabasePath,
                sdkPort,
                gatePort);
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            try
            {
                await WaitForSdkAsync(process, sdkPort, TimeSpan.FromSeconds(90));
                await VerifySdkLoginAsync(sdkPort);
                Assert.False(process.HasExited);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            string serverOutput = await standardOutput;
            string serverError = await standardError;
            Assert.DoesNotContain("Failed to start application", serverOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fatal", serverOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, serverError);

            NetPlayer stored = await ReadPlayerAsync(playerDatabasePath, snapshot.Manifest.OfficialUid);
            Assert.Equal("42", stored.AccountId);
            Assert.Equal(mapping.Profile.ToByteArray(), stored.Profile.ToByteArray());
            Assert.Equal(mapping.State.ToByteArray(), stored.State.ToByteArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static Process StartServer(
        string serverPath,
        string workingDirectory,
        string resourcesPath,
        string playerDatabasePath,
        string accountDatabasePath,
        int sdkPort,
        int gatePort)
    {
        var startInfo = new ProcessStartInfo("dotnet") {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(serverPath);
        startInfo.Environment["SL__GenerateRsaKeys"] = "false";
        startInfo.Environment["SL__Game__ResourcesPath"] = resourcesPath;
        startInfo.Environment["SL__DbGate__ConnectionString"] = SqliteConnectionString(playerDatabasePath);
        startInfo.Environment["SL__Sdk__Database__ConnectionString"] = SqliteConnectionString(accountDatabasePath);
        startInfo.Environment["SL__Sdk__BindAddress"] = IPAddress.Loopback.ToString();
        startInfo.Environment["SL__Sdk__BindPort"] = sdkPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["SL__Sdk__SkipSignatureCheck"] = "true";
        startInfo.Environment["SL__Gate__BindAddress"] = IPAddress.Loopback.ToString();
        startInfo.Environment["SL__Gate__BindPort"] = gatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["SL__Gate__ServePort"] = gatePort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Starlight.");
    }

    private static async Task WaitForSdkAsync(Process process, int sdkPort, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Starlight exited early with code {process.ExitCode}.");
            }

            try
            {
                string response = await client.GetStringAsync(
                    $"http://{IPAddress.Loopback}:{sdkPort}/",
                    cancellation.Token);
                if (response.Contains("Starlight", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(250, cancellation.Token);
        }

        throw new TimeoutException("Starlight SDK did not become ready before the smoke-test timeout.");
    }

    private static async Task VerifySdkLoginAsync(int sdkPort)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("x-rpc-device_id", "synthetic-smoke-device");
        client.DefaultRequestHeaders.Add("x-rpc-language", "en");
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"http://{IPAddress.Loopback}:{sdkPort}/hk4e_global/mdk/shield/api/login",
            new {
                account = SyntheticUsername,
                password = SyntheticPassword,
                is_crypto = false,
                game_key = "hk4e_global"
            });
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(body);
        JsonElement root = document.RootElement;
        Assert.Equal(expected: 0, root.GetProperty("retcode").GetInt32());
        JsonElement account = root.GetProperty("data").GetProperty("account");
        Assert.Equal(expected: 42u, account.GetProperty("id").GetUInt32());
        Assert.Equal(SyntheticUsername, account.GetProperty("name").GetString());
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int ReserveUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static string SqliteConnectionString(string path) =>
        new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

    private static async Task<NetPlayer> ReadPlayerAsync(string databasePath, uint uid)
    {
        var options = new DbContextOptionsBuilder<StarlightDbContext>()
            .UseSqlite(SqliteConnectionString(databasePath))
            .Options;
        await using var database = new StarlightDbContext(options);
        Player player = await database.Players
            .AsNoTracking()
            .Include(candidate => candidate.Profile)
            .SingleAsync(candidate => candidate.Id == uid);
        return player.Serialize();
    }
}

internal sealed class ServerSmokeFactAttribute : FactAttribute
{
    public ServerSmokeFactAttribute()
    {
        string? repositoryRoot = RealResourceArchive.FindRepositoryRoot();
        if (RealResourceArchive.Find() is null || FindServer(repositoryRoot) is null)
        {
            Skip = "Run scripts/verify-offline.ps1 with real resources to enable the server smoke test.";
        }
    }

    public static string? FindServer(string? repositoryRoot)
    {
        if (repositoryRoot is null)
        {
            return null;
        }

        string path = Path.Combine(
            repositoryRoot,
            "vendor",
            "Starlight",
            "Source",
            "Starlight",
            "bin",
            "Release",
            "net10.0",
            "Starlight.dll");
        return File.Exists(path) ? path : null;
    }
}
