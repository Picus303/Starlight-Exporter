using StarlightExporter.Official;
using StarlightExporter.Snapshot;

namespace StarlightExporter.Probe;

internal static class LiveProbeRunner
{
    public static async Task RunAsync(
        string stage,
        string runtimePath,
        string credentialsPath,
        string? snapshotPath,
        HttpClient httpClient,
        OfficialDispatchClient dispatch,
        OfficialClientProfile profile,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OfficialProbeRuntimeConfig config = OfficialProbeRuntimeConfig.Load(
            runtimePath, requireAuthHeaders: stage != "sdk-preflight");
        if (stage is "gate-login-live" or "snapshot-probe" or "snapshot-export")
        {
            config.RequireLoginInputs();
        }
        var attributes = new ConfiguredDeviceAttributeCollector(config.ExtFields);
        var initializer = new OfficialSdkInitializer(
            new OfficialSdkBootstrapClient(httpClient, OfficialSdkBootstrapOptions.WindowsGlobal700),
            new OfficialDeviceFingerprintExchange(httpClient, OfficialDeviceFingerprintOptions.WindowsGlobal700));
        var authOptions = new OfficialPasswordAuthOptions
        {
            Endpoint = OfficialPasswordAuthOptions.WindowsGlobal700Endpoint,
            SdkPublicKeyPem = OfficialPasswordAuthOptions.WindowsGlobal700PublicKeyPem,
            Headers = config.SdkHeaders(),
        };
        string statePath = Path.Combine(".local", "official-sdk-state.json");
        Directory.CreateDirectory(".local");
        PreparedSdkDevice prepared = await config.PrepareDeviceAsync(statePath, cancellationToken);
        string deviceUniqueIdentifier = prepared.UniqueIdentifier;
        await output.WriteLineAsync(
            $"sdk_registry_state_imported={Bool(prepared.RegistryStateImported)}");
        ProbeCredentialFile? credentials = null;
        async Task<OfficialPasswordCredentials> ReadCredentialsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            credentials = ProbeCredentialFile.Load(credentialsPath);
            if (stage is not ("auth-probe" or "combo-probe")
                && string.IsNullOrWhiteSpace(credentials.RegionName))
            {
                throw new ProbeConfigurationException("The local credentials file lacks a region.");
            }
            return await Task.FromResult(credentials.Credentials);
        }
        async Task ObserveInitializationAsync(
            OfficialSdkInitializationResult sdk,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                WindowsOfficialRuntimeSources.PersistSdkState(sdk.Installation);
            }
            await WriteFingerprintStatusAsync(
                output, sdk.FingerprintOrigin, sdk.FingerprintRejectionRetcode);
        }

        try
        {
            if (stage == "sdk-preflight")
            {
                OfficialSdkInitializationResult preflight = await initializer.InitializeAsync(
                    statePath, deviceUniqueIdentifier, config.FingerprintRuntime(),
                    attributes, cancellationToken);
                if (OperatingSystem.IsWindows())
                {
                    WindowsOfficialRuntimeSources.PersistSdkState(preflight.Installation);
                }
                await output.WriteLineAsync($"bootstrap_shield_reached={Bool(preflight.Bootstrap.ShieldConfigReached)}");
                await output.WriteLineAsync($"bootstrap_combo_reached={Bool(preflight.Bootstrap.ComboConfigReached)}");
                await WriteFingerprintStatusAsync(
                    output, preflight.FingerprintOrigin, preflight.FingerprintRejectionRetcode);
                await output.WriteLineAsync("probe_status=succeeded");
                return;
            }
            OfficialSdkAuthResult auth = await new OfficialSdkAuthFlow(
                httpClient,
                initializer,
                authOptions,
                ObserveInitializationAsync).RunAsync(
                    statePath, deviceUniqueIdentifier, config.FingerprintRuntime(),
                    attributes, ReadCredentialsAsync, cancellationToken);
            if (OperatingSystem.IsWindows())
            {
                WindowsOfficialRuntimeSources.PersistSdkState(auth.Installation);
            }
            await output.WriteLineAsync("sdk_auth=accepted");
            await output.WriteLineAsync($"bootstrap_shield_reached={Bool(auth.Bootstrap.ShieldConfigReached)}");
            await output.WriteLineAsync($"bootstrap_combo_reached={Bool(auth.Bootstrap.ComboConfigReached)}");
            if (stage == "auth-probe")
            {
                await output.WriteLineAsync("probe_status=succeeded");
                return;
            }

            OfficialSdkRequestHeaders headers = authOptions.Headers.WithInstallation(auth.Installation);
            using var combo = new OfficialComboSessionExchange(httpClient, new OfficialComboOptions
            {
                Endpoint = profile.ComboEndpoint
                    ?? throw new ProbeConfigurationException("The selected Combo endpoint is unavailable."),
                DeviceId = auth.Installation.DeviceId,
                ApplicationId = profile.ApplicationId,
                ChannelId = profile.ChannelId,
                Headers = headers,
            }, profile.ComboHmacKey
                ?? throw new ProbeConfigurationException("The selected Combo signing key is unavailable."));
            ComboSession session = await combo.ExchangeAsync(auth.Session, cancellationToken);
            await output.WriteLineAsync("combo=accepted");
            if (stage == "combo-probe")
            {
                await output.WriteLineAsync("probe_status=succeeded");
                return;
            }

            string regionName = credentials!.RegionName!;
            OfficialCurrentRegion region = await dispatch.ResolveRegionAsync(
                profile, regionName, session, cancellationToken);
            await output.WriteLineAsync("regional_dispatch=accepted");
            await output.WriteLineAsync($"gate_key_id={profile.KeyId}");
            await output.WriteLineAsync($"gate_ticket_present={Bool(!region.ConnectGateTicket.IsEmpty)}");
            await output.WriteLineAsync($"gate_secret_bytes={region.ClientSecretKey.Length}");
            if (stage == "dispatch-probe")
            {
                await output.WriteLineAsync("probe_status=succeeded");
                return;
            }

            if (stage == "gate-token-live")
            {
                var trace = new GateMetadataTrace();
                try
                {
                    GateTokenProbeResult token = await OfficialGateProbeClient.ProbeTokenAsync(
                        session, region, profile,
                        new OfficialGateProbeOptions { MetadataTrace = trace }, cancellationToken);
                    await output.WriteLineAsync($"gate_token_accepted={Bool(token.SessionRekeySucceeded)}");
                    await output.WriteLineAsync("probe_status=succeeded");
                }
                finally
                {
                    await WriteTraceAsync(output, trace.Records);
                }
                return;
            }

            OfficialPlayerLoginProfile login = config.LoginProfile(auth.Installation);
            if (stage == "gate-login-live")
            {
                var trace = new GateMetadataTrace();
                try
                {
                    GateLoginProbeResult accepted = await OfficialGateProbeClient.ProbeLoginAsync(
                        session, region, profile, login,
                        new OfficialGateProbeOptions { MetadataTrace = trace }, cancellationToken);
                    await output.WriteLineAsync($"gate_login_accepted={Bool(accepted.PlayerLoginResponseReceived)}");
                    await output.WriteLineAsync($"response_uid_matches={Bool(accepted.PlayerUidMatches)}");
                    await output.WriteLineAsync("probe_status=succeeded");
                }
                finally
                {
                    await WriteTraceAsync(output, trace.Records);
                }
                return;
            }

            var snapshotTrace = new GateMetadataTrace();
            try
            {
                await using LiveGateMessageSource source = await LiveGateMessageSource.ConnectAsync(
                    session, region, profile, login,
                    new OfficialGateSessionOptions { MetadataTrace = snapshotTrace }, cancellationToken);
                OfficialSnapshot snapshot = await new OfficialSnapshotCollector().CollectAsync(
                    new OfficialCaptureContext(source.PlayerUid, source.RegionName, DateTimeOffset.UtcNow),
                    source, cancellationToken);
                SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
                if (!validation.IsValid)
                {
                    throw new ProbeConfigurationException("The captured snapshot did not pass validation.");
                }
                await OfficialSnapshotSerializer.WriteNewAsync(snapshotPath!, snapshot, cancellationToken);
                await output.WriteLineAsync($"snapshot_materials={snapshot.Materials.Count}");
                await output.WriteLineAsync($"snapshot_weapons={snapshot.Weapons.Count}");
                await output.WriteLineAsync($"snapshot_avatars={snapshot.Avatars.Count}");
                await output.WriteLineAsync($"snapshot_teams={snapshot.Teams.Count}");
                await output.WriteLineAsync($"snapshot_unsupported={snapshot.Unsupported.Count}");
                await output.WriteLineAsync(stage == "snapshot-export"
                    ? "snapshot_status=succeeded"
                    : "probe_status=succeeded");
            }
            finally
            {
                await WriteTraceAsync(output, snapshotTrace.Records);
            }
        }
        catch (OfficialConnectivityException) when (attributes.MissingNames.Count != 0)
        {
            // Names are supplied by getExtList and are safe to report; values are not.
            await output.WriteLineAsync(
                $"missing_device_attributes={string.Join(',', attributes.MissingNames)}");
            throw;
        }
    }

    private static async Task WriteTraceAsync(
        TextWriter output, IReadOnlyList<GateMetadataTraceRecord> trace)
    {
        foreach (GateMetadataTraceRecord record in trace)
        {
            await output.WriteLineAsync(
                $"trace={record.Sequence:D3};elapsed_ms={record.ElapsedMilliseconds};phase={record.Phase};direction={record.Direction};cmd_id={record.CommandId};type={record.MessageType};bytes={record.SerializedBodyBytes};chunked={Bool(record.Chunked)};duplicate={Bool(record.Duplicate)};presence={record.FieldPresenceHash};retcode={record.Retcode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}");
        }
    }

    private static async Task WriteFingerprintStatusAsync(
        TextWriter output,
        OfficialDeviceFingerprintOrigin origin,
        int? rejectionRetcode)
    {
        await output.WriteLineAsync(
            $"server_fingerprint_acquired={Bool(origin == OfficialDeviceFingerprintOrigin.ServerRefreshed)}");
        await output.WriteLineAsync($"device_fingerprint_origin={origin switch
        {
            OfficialDeviceFingerprintOrigin.ServerRefreshed => "server",
            OfficialDeviceFingerprintOrigin.Persisted => "persisted",
            OfficialDeviceFingerprintOrigin.GeneratedFallback => "generated_fallback",
            _ => "unknown",
        }}");
        if (rejectionRetcode is { } retcode)
        {
            await output.WriteLineAsync(
                $"device_fingerprint_rejection_retcode={retcode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    private static string Bool(bool value) => value.ToString().ToLowerInvariant();
}
