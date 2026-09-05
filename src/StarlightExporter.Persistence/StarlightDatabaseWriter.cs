using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Starlight.Database;
using Starlight.DbGate;
using Starlight.DbGate.Models;
using Starlight.Rpc.Proto;

namespace StarlightExporter.Persistence;

public static class StarlightDatabaseWriter
{
    public static async Task<StarlightDatabaseWriteResult> WriteNewAsync(
        StarlightDatabaseWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        string outputPath = Path.GetFullPath(request.OutputPath);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"The output path already exists: '{outputPath}'.");
        }

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(request));
        Directory.CreateDirectory(outputDirectory);

        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            NetPlayer verified = await WriteAndVerifyAsync(request, temporaryPath, cancellationToken);
            VerifyExpectedPlayer(request, verified);

            SqliteConnection.ClearAllPools();
            File.Move(temporaryPath, outputPath);

            return new StarlightDatabaseWriteResult(
                outputPath,
                verified.Uid,
                verified.AccountId,
                verified.State.Materials.Count,
                verified.State.Weapons.Count,
                verified.State.Avatars.Count,
                verified.State.AvatarTeams.Count);
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

    private static async Task<NetPlayer> WriteAndVerifyAsync(
        StarlightDatabaseWriteRequest request,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddStarlightDbContext<StarlightDbContext>(new DbGateConfig {
            Provider = ProviderType.Sqlite,
            ConnectionString = connectionString
        });

        using IHost host = builder.Build();
        await host.StartAsync(cancellationToken);

        try
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            StarlightDbContext db = scope.ServiceProvider.GetRequiredService<StarlightDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Players.Add(new Player {
                Id = request.PlayerUid,
                AccountId = request.PrivateAccountId,
                Profile = new PlayerProfile {
                    Nickname = request.Profile.Nickname,
                    Signature = request.Profile.Signature,
                    PictureId = request.Profile.PictureId,
                    NameCardId = request.Profile.NameCardId
                },
                State = request.State.ToByteArray()
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            db.ChangeTracker.Clear();
            Player stored = await db.Players
                .AsNoTracking()
                .Include(player => player.Profile)
                .SingleAsync(player => player.Id == request.PlayerUid, cancellationToken);
            return stored.Serialize();
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }
    }

    private static void ValidateRequest(StarlightDatabaseWriteRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentOutOfRangeException.ThrowIfZero(request.PlayerUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PrivateAccountId);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.State);

        if (request.PrivateAccountId.Length > 64)
        {
            throw new ArgumentException("The private account ID cannot exceed 64 characters.", nameof(request));
        }

        NetPlayerState reparsed = NetPlayerState.Parser.ParseFrom(request.State.ToByteArray());
        if (!request.State.ToByteArray().SequenceEqual(reparsed.ToByteArray()))
        {
            throw new ArgumentException("The mapped state does not survive a protobuf round-trip.", nameof(request));
        }
    }

    private static void VerifyExpectedPlayer(StarlightDatabaseWriteRequest request, NetPlayer verified)
    {
        if (verified.Uid != request.PlayerUid
            || !string.Equals(verified.AccountId, request.PrivateAccountId, StringComparison.Ordinal)
            || !verified.State.ToByteArray().SequenceEqual(request.State.ToByteArray())
            || !verified.Profile.ToByteArray().SequenceEqual(request.Profile.ToByteArray()))
        {
            throw new InvalidDataException("The player read from the database differs from the requested import.");
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
