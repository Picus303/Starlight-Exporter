using StarlightExporter.Official;

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
                "gate-token" when args.Length == 2 =>
                    await RunGateTokenAsync(client, args[1], output, cancellationToken),
                "gate-login" when args.Length == 2 =>
                    await RunGateLoginAsync(client, args[1], output, cancellationToken),
                _ => InvalidArguments(error),
            };
        }
        catch (OfficialConnectivityException exception)
        {
            await error.WriteLineAsync($"probe_status=failed");
            await error.WriteLineAsync($"error_category={OfficialConnectivityDiagnostic.Code(exception.Error)}");
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

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("StarlightExporter.Probe (opt-in official connectivity checks)");
        output.WriteLine("  dispatch-list       Query public OS Global region metadata.");
        output.WriteLine("  region <name>       Resolve and verify one public regional dispatch.");
        output.WriteLine("  gate-token <name>  Run an authorized token-only Gate probe from runtime environment values.");
        output.WriteLine("  gate-login <name>  Run token + PlayerLogin probes from runtime environment values.");
        output.WriteLine("Set STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE when the pinned verification key is incompatible.");
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
            OfficialClientProfile.OsGlobalV70,
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
            OfficialClientProfile.OsGlobalV70,
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
        uint expectedUid = RequiredUInt32("STARLIGHT_EXPORTER_OFFICIAL_UID");
        return new ExistingComboSessionProvider(ComboSession.Create(
            accountUid,
            accountToken,
            accountType,
            isGuest: false,
            countryCode,
            expectedUid));
    }

    private static OfficialPlayerLoginProfile ReadLoginProfile() => new()
    {
        PlatformName = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_PLATFORM_NAME"),
        DeviceInfo = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_INFO"),
        DeviceName = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_NAME"),
        DeviceUuid = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_DEVICE_UUID"),
        SystemVersion = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_SYSTEM_VERSION"),
        Checksum = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_CHECKSUM"),
        ChecksumClientVersion = RequiredEnvironment("STARLIGHT_EXPORTER_LOGIN_CHECKSUM_CLIENT_VERSION"),
        ClientVersionHash = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_LOGIN_CLIENT_VERSION_HASH") ?? string.Empty,
        UserAgent = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_LOGIN_USER_AGENT") ?? string.Empty,
        RegistrationPlatform = OptionalUInt32("STARLIGHT_EXPORTER_LOGIN_REGISTRATION_PLATFORM", 3),
    };

    private static StarlightRegionCrypto CreateRegionCrypto()
    {
        string? path = Environment.GetEnvironmentVariable(
            "STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return StarlightRegionCrypto.CreatePinned();
        }
        if (!File.Exists(path))
        {
            throw new ProbeConfigurationException(
                "STARLIGHT_EXPORTER_REGION_VERIFY_KEY_FILE does not exist.");
        }

        return StarlightRegionCrypto.CreatePinnedWithVerificationKey(File.ReadAllText(path));
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

    private static uint RequiredUInt32(string name)
    {
        string value = RequiredEnvironment(name);
        if (!uint.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint result)
            || result == 0)
        {
            throw new ProbeConfigurationException($"Environment variable {name} must be a non-zero UInt32.");
        }
        return result;
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
                $"trace={record.Sequence:D3};elapsed_ms={record.ElapsedMilliseconds};phase={record.Phase};direction={record.Direction};cmd_id={record.CommandId};type={record.MessageType};bytes={record.SerializedBodyBytes};retcode={record.Retcode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}");
        }
    }

    private static string Bool(bool value) => value.ToString().ToLowerInvariant();

    private sealed class ProbeConfigurationException(string message) : Exception(message);
}
