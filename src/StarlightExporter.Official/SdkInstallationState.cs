using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace StarlightExporter.Official;

public sealed record SdkInstallationState
{
    private const int SeedIdBytes = 8;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    public required string DeviceId { get; init; }
    public string SeedId { get; init; } = string.Empty;
    public string SeedTime { get; init; } = string.Empty;
    public string? DeviceFingerprint { get; init; }

    public static SdkInstallationState Create(
        string deviceUniqueIdentifier,
        TimeProvider? clock = null,
        Func<int, byte[]>? randomBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUniqueIdentifier);
        TimeProvider selectedClock = clock ?? TimeProvider.System;
        long unixTicks = selectedClock.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks;
        long unixMilliseconds = unixTicks / TimeSpan.TicksPerMillisecond;
        if (unixMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clock), "The SDK clock predates the Unix epoch.");
        }

        string deviceId = deviceUniqueIdentifier
            + unixMilliseconds.ToString(CultureInfo.InvariantCulture);
        byte[] seedBytes = (randomBytes ?? RandomNumberGenerator.GetBytes)(SeedIdBytes);
        if (seedBytes.Length != SeedIdBytes)
        {
            throw new InvalidOperationException("The SDK seed source returned an invalid byte count.");
        }

        try
        {
            var created = new SdkInstallationState
            {
                DeviceId = deviceId,
                SeedId = Convert.ToHexStringLower(seedBytes),
                SeedTime = unixMilliseconds.ToString(CultureInfo.InvariantCulture),
            };
            Validate(created);
            return created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedBytes);
        }
    }

    public SdkInstallationState WithServerFingerprint(string fingerprint)
        => WithDeviceFingerprint(fingerprint);

    public SdkInstallationState WithDeviceFingerprint(string fingerprint)
    {
        if (!IsValidFingerprint(fingerprint))
        {
            throw new ArgumentException("The device fingerprint has an invalid format.", nameof(fingerprint));
        }
        return this with { DeviceFingerprint = fingerprint };
    }

    public static async Task<SdkInstallationState> LoadOrCreateAsync(
        string path,
        string deviceUniqueIdentifier,
        TimeProvider? clock = null,
        Func<int, byte[]>? randomBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The SDK installation state directory does not exist.");
        }

        await using FileStream installationLock = await AcquireLockAsync(
            fullPath + ".lock", cancellationToken);

        if (File.Exists(fullPath))
        {
            SdkInstallationState? saved;
            await using (FileStream input = File.OpenRead(fullPath))
            {
                saved = await JsonSerializer.DeserializeAsync<SdkInstallationState>(
                    input, cancellationToken: cancellationToken);
            }
            if (saved is null)
            {
                throw new InvalidDataException("The SDK installation state is empty.");
            }

            if (!HasValidSeeds(saved))
            {
                SdkInstallationState generated = Create(deviceUniqueIdentifier, clock, randomBytes);
                saved = saved with
                {
                    SeedId = generated.SeedId,
                    SeedTime = generated.SeedTime,
                };
                await saved.SaveCoreAsync(fullPath, cancellationToken);
            }
            Validate(saved);
            return saved;
        }

        SdkInstallationState created = Create(deviceUniqueIdentifier, clock, randomBytes);
        await created.SaveCoreAsync(fullPath, cancellationToken);
        return created;
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(this);
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
        {
            throw new DirectoryNotFoundException("The SDK installation state directory does not exist.");
        }
        await SaveCoreAsync(fullPath, cancellationToken);
    }

    public static bool IsValidFingerprint(string? fingerprint) =>
        fingerprint is { Length: 10 or 11 } && fingerprint.All(char.IsAsciiDigit);

    public override string ToString() => "SdkInstallationState { Device = [REDACTED] }";

    private async Task SaveCoreAsync(string fullPath, CancellationToken cancellationToken)
    {
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, this, cancellationToken: cancellationToken);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<FileStream> AcquireLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
    }

    private static void Validate(SdkInstallationState state)
    {
        ValidateDeviceId(state.DeviceId);
        if (!HasValidSeeds(state))
        {
            throw new InvalidDataException("The SDK installation seeds are invalid.");
        }
        if (state.DeviceFingerprint is not null && !IsValidFingerprint(state.DeviceFingerprint))
        {
            throw new InvalidDataException("The SDK installation fingerprint is invalid.");
        }
    }

    private static bool HasValidSeeds(SdkInstallationState state) =>
        state.SeedId.Length == SeedIdBytes * 2
        && state.SeedId.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character))
        && state.SeedTime.Length is > 0 and <= 32
        && state.SeedTime.All(char.IsAsciiDigit);

    private static void ValidateDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || deviceId.Length > 256
            || !string.Equals(Uri.EscapeDataString(deviceId), deviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The SDK device ID is invalid.");
        }
    }
}
