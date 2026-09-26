using StarlightExporter.Cli;
using StarlightExporter.Official;
using StarlightExporter.Probe;

return await ProbeApplication.RunAsync(args, Console.Out, Console.Error);

internal static class ProbeApplication
{
    private const int UsageError = 2;
    private const int ProbeFailed = 3;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            WriteUsage(output);
            return args.Length == 0 ? UsageError : 0;
        }

        try
        {
            if (args[0] != "runtime-check")
            {
                RequireCurrentBuildProfile();
            }
            using var httpClient = new HttpClient();
            using StarlightRegionCrypto crypto = CreateRegionCrypto();
            var client = new OfficialDispatchClient(httpClient, crypto);
            var probe = new OfficialDispatchProbe(client);
            return args[0] switch
            {
                "dispatch-list" when args.Length == 1 =>
                    await RunDispatchListAsync(probe, output, cancellationToken),
                "region" when args.Length == 2 =>
                    await RunRegionAsync(probe, args[1], output, cancellationToken),
                "sdk-ext-list" when args.Length == 1 =>
                    await RunSdkExtListAsync(httpClient, output, cancellationToken),
                "sdk-bootstrap" when args.Length == 1 =>
                    await RunSdkBootstrapAsync(httpClient, output, cancellationToken),
                "runtime-check" when args.Length == 2 =>
                    await RunRuntimeCheckAsync(args[1], output, cancellationToken),
                "gate-token" when args.Length == 2 =>
                    await RunGateTokenAsync(client, args[1], output, cancellationToken),
                "gate-login" when args.Length == 2 =>
                    await RunGateLoginAsync(client, args[1], output, cancellationToken),
                "sdk-preflight" when args.Length == 2 =>
                    await RunLiveStageAsync(args, null, httpClient, client, output, cancellationToken),
                "auth-probe" or "combo-probe" or "dispatch-probe" or "gate-token-live" or "gate-login-live"
                    when args.Length == 3 =>
                    await RunLiveStageAsync(args, null, httpClient, client, output, cancellationToken),
                "snapshot-probe" when args.Length == 4 =>
                    await RunLiveStageAsync(args, args[3], httpClient, client, output, cancellationToken),
                "snapshot-export" when args.Length >= 10 =>
                    await RunSnapshotExportAsync(
                        args, httpClient, client, output, error, cancellationToken),
                _ => InvalidArguments(error),
            };
        }
        catch (OfficialConnectivityException exception)
        {
            await error.WriteLineAsync($"probe_status=failed");
            await error.WriteLineAsync($"error_category={OfficialConnectivityDiagnostic.Code(exception.Error)}");
            if (exception.Retcode is { } retcode)
            {
                await error.WriteLineAsync($"retcode={retcode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            await error.WriteLineAsync($"error_message={OfficialConnectivityDiagnostic.SafeMessage(exception.Error)}");
            return ProbeFailed;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("probe_status=cancelled");
            return ProbeFailed;
        }
        catch (ProbeConfigurationException exception)
        {
            await error.WriteLineAsync("probe_status=not_run");
            await error.WriteLineAsync($"configuration_error={exception.Message}");
            return UsageError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or ArgumentException
            or InvalidDataException)
        {
            await error.WriteLineAsync("probe_status=not_run");
            await error.WriteLineAsync("configuration_error=The local probe configuration could not be used.");
            return UsageError;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("probe_status=failed");
            await error.WriteLineAsync("error_category=UNEXPECTED");
            return ProbeFailed;
        }
    }

    private static async Task<int> RunDispatchListAsync(
        OfficialDispatchProbe probe,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        DispatchListProbeResult result = await probe.ProbeListAsync(
            OfficialClientProfile.OsGlobalV70,
            cancellationToken);
        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync("retcode=0");
        await output.WriteLineAsync($"profile.version={result.Version}");
        await output.WriteLineAsync($"profile.protocol={result.ProtocolVersion}");
        await output.WriteLineAsync($"profile.language={result.Language}");
        await output.WriteLineAsync($"profile.platform={result.Platform}");
        await output.WriteLineAsync($"profile.binary={result.Binary}");
        await output.WriteLineAsync($"profile.channel_id={result.ChannelId}");
        await output.WriteLineAsync($"profile.sub_channel_id={result.SubChannelId}");
        await output.WriteLineAsync($"enable_login_pc={result.EnableLoginPc.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"client_secret_key_bytes={result.ClientSecretKeyBytes}");
        await output.WriteLineAsync($"region_count={result.Regions.Count}");
        foreach (DispatchRegionSummary region in result.Regions)
        {
            await output.WriteLineAsync($"region={region.Name};title={region.Title};type={region.Type}");
        }
        return 0;
    }

    private static async Task<int> RunRegionAsync(
        OfficialDispatchProbe probe,
        string regionName,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RegionalDispatchProbeResult result = await probe.ProbeRegionAsync(
            OfficialClientProfile.OsGlobalV70,
            regionName,
            cancellationToken);
        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync("retcode=0");
        await output.WriteLineAsync($"region={result.RegionName}");
        await output.WriteLineAsync($"payload_format={result.PayloadFormat}");
        await output.WriteLineAsync($"crypto_verified={(result.PayloadFormat == OfficialRegionalPayloadFormat.EncryptedJsonEnvelope).ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"key_id={result.KeyId}");
        await output.WriteLineAsync($"gate_host_present={result.GateHostPresent.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"gate_port={result.GatePort}");
        await output.WriteLineAsync($"domain_mode={result.UsesDomainName.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"connect_gate_ticket_present={result.ConnectGateTicketPresent.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"client_data_version={result.ClientDataVersion}");
        await output.WriteLineAsync($"client_silence_data_version={result.ClientSilenceDataVersion}");
        await output.WriteLineAsync($"game_biz={result.GameBiz}");
        await output.WriteLineAsync($"resource_url_present={result.ResourceUrlPresent.ToString().ToLowerInvariant()}");
        await output.WriteLineAsync($"data_url_present={result.DataUrlPresent.ToString().ToLowerInvariant()}");
        return 0;
    }

    private static int InvalidArguments(TextWriter error)
    {
        error.WriteLine("Invalid probe command. Use --help for usage.");
        return UsageError;
    }

    private static async Task<int> RunSdkExtListAsync(
        HttpClient httpClient,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var exchange = new OfficialDeviceFingerprintExchange(
            httpClient, OfficialDeviceFingerprintOptions.WindowsGlobal700);
        IReadOnlyList<string> names = await exchange.GetRequestedAttributesAsync(cancellationToken);
        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync($"requested_attribute_count={names.Count}");
        foreach (string name in names)
        {
            await output.WriteLineAsync($"requested_attribute={name}");
        }
        return 0;
    }

    private static async Task<int> RunSdkBootstrapAsync(
        HttpClient httpClient,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var bootstrap = new OfficialSdkBootstrapClient(
            httpClient, OfficialSdkBootstrapOptions.WindowsGlobal700);
        OfficialSdkBootstrapResult result = await bootstrap.AttemptAsync(cancellationToken);
        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync($"shield_config_reached={Bool(result.ShieldConfigReached)}");
        await output.WriteLineAsync($"combo_config_reached={Bool(result.ComboConfigReached)}");
        return 0;
    }

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("StarlightExporter.Probe (opt-in official connectivity checks)");
        output.WriteLine("  dispatch-list       Query public OS Global region metadata.");
        output.WriteLine("  region <name>       Resolve and verify one public regional dispatch.");
        output.WriteLine("  sdk-ext-list       Read public DeviceFp attribute names without machine or account data.");
        output.WriteLine("  sdk-bootstrap      Check public SDK config routes without machine or account data.");
        output.WriteLine("  runtime-check <runtime.json>  Validate local producers without network or credentials.");
        output.WriteLine("  gate-token <name>  Run an authorized token-only Gate probe from runtime environment values.");
        output.WriteLine("  gate-login <name>  Run token + PlayerLogin probes from runtime environment values.");
        output.WriteLine("  sdk-preflight <runtime.json>  Refresh DeviceFp without reading account credentials.");
        output.WriteLine("  auth-probe <runtime.json> <official.env>       B1: SDK auth only.");
        output.WriteLine("  combo-probe <runtime.json> <official.env>      B1 then B2, one session.");
        output.WriteLine("  dispatch-probe <runtime.json> <official.env>   B1 through B3.");
        output.WriteLine("  gate-token-live <runtime.json> <official.env>  B1 through B4.");
        output.WriteLine("  gate-login-live <runtime.json> <official.env>  B1 through B5.");
        output.WriteLine("  snapshot-probe <runtime.json> <official.env> <snapshot.json>  Capture B6/B7.");
        output.WriteLine(
            "  snapshot-export <runtime.json> <official.env> <snapshot.json> --resources <path> "
            + "--output <directory> --private-account-id <id> [--accounts-db <path>] "
            + "[--uid-mode preserve|allocate] [--strict]  Capture and build B8.");
        output.WriteLine("The bundled profile is 7.0.0/V70. Set STARLIGHT_EXPORTER_ALLOW_LEGACY_V70_PROBES=1 only for an intentional legacy-profile diagnostic run.");
        output.WriteLine("Set STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE when the pinned verification key is incompatible.");
    }

    private static void RequireCurrentBuildProfile()
    {
        if (Environment.GetEnvironmentVariable("STARLIGHT_EXPORTER_ALLOW_LEGACY_V70_PROBES") != "1")
        {
            throw new ProbeConfigurationException(
                "Only the Windows Global 7.0.0/V70 probe profile is configured; the reported current version is 7.1. Set STARLIGHT_EXPORTER_ALLOW_LEGACY_V70_PROBES=1 only for a deliberate legacy-profile diagnostic run.");
        }
    }

    private static async Task<int> RunGateTokenAsync(
        OfficialDispatchClient dispatchClient,
        string regionName,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OfficialCurrentRegion region = await dispatchClient.ResolveRegionAsync(
            OfficialClientProfile.OsGlobalV70,
            regionName,
            cancellationToken);
        ComboSession session = await ReadExistingSessionProvider().GetSessionAsync(cancellationToken);
        GateTokenProbeResult result = await OfficialGateProbeClient.ProbeTokenAsync(
            session,
            region,
            ReadGateProfile(),
            cancellationToken: cancellationToken);

        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync($"kcp_handshake={Bool(result.HandshakeSucceeded)}");
        await output.WriteLineAsync($"conversation_assigned={Bool(result.ConversationAssigned)}");
        await output.WriteLineAsync($"transport_token_assigned={Bool(result.TransportTokenAssigned)}");
        await output.WriteLineAsync($"initial_pad_derived={Bool(result.InitialPadDerived)}");
        await output.WriteLineAsync($"player_token_response_received={Bool(result.PlayerTokenResponseReceived)}");
        await output.WriteLineAsync($"response_uid_matches_expected={Bool(result.PlayerUidMatchesExpected)}");
        await output.WriteLineAsync($"key_id_matches={Bool(result.KeyIdMatches)}");
        await output.WriteLineAsync($"server_random_key_decrypted={Bool(result.ServerRandomKeyDecrypted)}");
        await output.WriteLineAsync($"server_signature_valid={Bool(result.ServerSignatureValid)}");
        await output.WriteLineAsync($"session_rekey={Bool(result.SessionRekeySucceeded)}");
        WriteTrace(output, result.Trace);
        return 0;
    }

    private static async Task<int> RunGateLoginAsync(
        OfficialDispatchClient dispatchClient,
        string regionName,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OfficialCurrentRegion region = await dispatchClient.ResolveRegionAsync(
            OfficialClientProfile.OsGlobalV70,
            regionName,
            cancellationToken);
        ComboSession session = await ReadExistingSessionProvider().GetSessionAsync(cancellationToken);
        GateLoginProbeResult result = await OfficialGateProbeClient.ProbeLoginAsync(
            session,
            region,
            ReadGateProfile(),
            ReadLoginProfile(),
            cancellationToken: cancellationToken);

        await output.WriteLineAsync("probe_status=succeeded");
        await output.WriteLineAsync($"player_token_response_received={Bool(result.Token.PlayerTokenResponseReceived)}");
        await output.WriteLineAsync($"session_rekey={Bool(result.Token.SessionRekeySucceeded)}");
        await output.WriteLineAsync($"player_login_response_received={Bool(result.PlayerLoginResponseReceived)}");
        await output.WriteLineAsync($"response_uid_matches={Bool(result.PlayerUidMatches)}");
        await output.WriteLineAsync($"relogin_required={Bool(result.ReloginRequired)}");
        WriteTrace(output, result.Trace);
        return 0;
    }

    private static ExistingComboSessionProvider ReadExistingSessionProvider()
    {
        string accountUid = RequiredEnvironment("STARLIGHT_EXPORTER_COMBO_ACCOUNT_UID");
        string accountToken = RequiredEnvironment("STARLIGHT_EXPORTER_COMBO_ACCOUNT_TOKEN");
        uint accountType = OptionalUInt32("STARLIGHT_EXPORTER_COMBO_ACCOUNT_TYPE", 1);
        string countryCode = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_COMBO_COUNTRY_CODE") ?? string.Empty;
        return new ExistingComboSessionProvider(ComboSession.Create(
            accountUid,
            accountToken,
            accountType,
            isGuest: false,
            countryCode));
    }

    private static async Task<int> RunLiveStageAsync(
        string[] args,
        string? snapshotPath,
        HttpClient httpClient,
        OfficialDispatchClient dispatchClient,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await LiveProbeRunner.RunAsync(args[0], args[1], args.Length >= 3 ? args[2] : string.Empty,
            snapshotPath,
            httpClient, dispatchClient, ReadGateProfile(), output, cancellationToken);
        return 0;
    }

    private static async Task<int> RunSnapshotExportAsync(
        string[] args,
        HttpClient httpClient,
        OfficialDispatchClient dispatchClient,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string[] databaseArguments = ["build-db", args[3], .. args[4..]];
        if (!HasRequiredBuildDatabaseArguments(databaseArguments))
        {
            throw new ProbeConfigurationException(
                "snapshot-export requires --resources, --output and --private-account-id.");
        }

        await LiveProbeRunner.RunAsync(
            "snapshot-export", args[1], args[2], args[3],
            httpClient, dispatchClient, ReadGateProfile(), output, cancellationToken);

        // The regular CLI reports the imported UID and may include inventory identifiers in
        // diagnostics. Keep those details out of the live-probe journal while retaining the
        // structured exit code for local troubleshooting.
        using var databaseOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var databaseError = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        int result = await CliApplication.RunAsync(
            databaseArguments, databaseOutput, databaseError, cancellationToken);
        if (result == CliApplication.Success)
        {
            await output.WriteLineAsync("db_ready=true");
            await output.WriteLineAsync("probe_status=succeeded");
        }
        else
        {
            await error.WriteLineAsync("database_export_status=failed");
            await error.WriteLineAsync(
                $"database_export_exit_code={result.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return result;
    }

    private static async Task<int> RunRuntimeCheckAsync(
        string runtimePath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OfficialProbeRuntimeConfig config = OfficialProbeRuntimeConfig.Load(runtimePath);
        config.RequireLoginInputs();
        var collector = new ConfiguredDeviceAttributeCollector(config.ExtFields);
        int available = 0;
        foreach (string name in OfficialDeviceFingerprintFields.KnownNames)
        {
            if (await collector.CollectAsync(name, cancellationToken) is not null)
            {
                available++;
            }
        }

        await output.WriteLineAsync(
            $"device_fp_known_fields={OfficialDeviceFingerprintFields.KnownNames.Count}");
        await output.WriteLineAsync($"device_fp_available_fields={available}");
        await output.WriteLineAsync(
            $"device_fp_unavailable_fields={collector.MissingNames.Count}");
        foreach (string name in collector.MissingNames.Distinct(StringComparer.Ordinal))
        {
            await output.WriteLineAsync($"unavailable_device_attribute={name}");
        }
        if (OperatingSystem.IsWindows())
        {
            WindowsGraphicsInfo? graphics = WindowsGraphicsSources.FirstAdapter();
            await output.WriteLineAsync($"dxgi_adapter_ready={Bool(graphics is not null)}");
            await output.WriteLineAsync(
                $"d3d_feature_ready={Bool(graphics?.DeviceCreationSucceeded == true)}");
        }
        await output.WriteLineAsync("player_login_inputs_ready=true");
        await output.WriteLineAsync("probe_status=succeeded");
        return 0;
    }

    private static bool HasRequiredBuildDatabaseArguments(string[] arguments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 2; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (name == "--strict")
            {
                continue;
            }
            if (name is not ("--resources" or "--output" or "--private-account-id"
                or "--accounts-db" or "--uid-mode")
                || ++index >= arguments.Length
                || string.IsNullOrWhiteSpace(arguments[index]))
            {
                return false;
            }
            seen.Add(name);
        }

        return seen.Contains("--resources")
            && seen.Contains("--output")
            && seen.Contains("--private-account-id");
    }

    private static OfficialPlayerLoginProfile ReadLoginProfile() => new()
    {
        DeviceInfo = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_INFO"),
        DeviceName = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_NAME"),
        DeviceUuid = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_UUID"),
        SystemVersion = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_SYSTEM_VERSION"),
        DeviceFingerprint = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_LOGIN_DEVICE_FP") ?? string.Empty,
        SecurityLibraryMd5 = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_LOGIN_SECURITY_LIBRARY_MD5") ?? string.Empty,
        ScreenWidth = checked((int)OptionalUInt32("STARLIGHT_EXPORTER_LOGIN_SCREEN_WIDTH", 0)),
        ScreenHeight = checked((int)OptionalUInt32("STARLIGHT_EXPORTER_LOGIN_SCREEN_HEIGHT", 0)),
    };

    private static StarlightRegionCrypto CreateRegionCrypto()
    {
        return StarlightRegionCrypto.CreatePinnedWithVerificationKey(
            ReadGateProfile().GateServerPublicKeyPem);
    }

    private static OfficialClientProfile ReadGateProfile()
    {
        string? path = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return OfficialClientProfile.OsGlobalV70;
        }
        if (!File.Exists(path))
        {
            throw new ProbeConfigurationException(
                "STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE does not exist.");
        }

        return OfficialClientProfile.OsGlobalV70 with
        {
            GateServerPublicKeyPem = File.ReadAllText(path),
        };
    }

    private static string RequiredEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProbeConfigurationException($"Required environment variable {name} is missing.");
        }
        return value;
    }

    private static uint OptionalUInt32(string name, uint defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        if (!uint.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint result))
        {
            throw new ProbeConfigurationException($"Environment variable {name} must be a UInt32.");
        }
        return result;
    }

    private static void WriteTrace(
        TextWriter output,
        IReadOnlyList<GateMetadataTraceRecord> trace)
    {
        foreach (GateMetadataTraceRecord record in trace)
        {
            output.WriteLine(
                $"trace={record.Sequence:D3};elapsed_ms={record.ElapsedMilliseconds};phase={record.Phase};direction={record.Direction};cmd_id={record.CommandId};type={record.MessageType};bytes={record.SerializedBodyBytes};chunked={Bool(record.Chunked)};duplicate={Bool(record.Duplicate)};presence={record.FieldPresenceHash};retcode={record.Retcode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}");
        }
    }

    private static string Bool(bool value) => value.ToString().ToLowerInvariant();

    private static string Bool(bool? value) => value is null ? "n/a" : Bool(value.Value);

}
