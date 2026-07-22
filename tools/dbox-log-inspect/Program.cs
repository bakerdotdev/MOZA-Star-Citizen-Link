using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MozaStarCitizen.App.ForceFeedback;
using MozaStarCitizen.App.Models;
using MozaStarCitizen.App.Telemetry;
using MozaStarCitizen.App.Telemetry.DBoxSdkLog;

if (args.Length > 0 &&
    string.Equals(args[0], "--observe", StringComparison.OrdinalIgnoreCase))
{
    if (!TryParseObserverArguments(args, out var observerPath, out var idleTimeout))
    {
        PrintUsage();
        return 1;
    }

    return await Observe(observerPath!, idleTimeout);
}

if (args.Length == 0 ||
    (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RunSelfTest();
}

var validateOnly = args.Length == 2 &&
    string.Equals(args[0], "--validate", StringComparison.OrdinalIgnoreCase);
if (!validateOnly && args.Length != 1)
{
    PrintUsage();
    return 1;
}

return Inspect(validateOnly ? args[1] : args[0], emitFrames: !validateOnly);

static bool TryParseObserverArguments(
    string[] arguments,
    out string? path,
    out TimeSpan idleTimeout)
{
    path = null;
    idleTimeout = TimeSpan.FromSeconds(30);
    if (arguments.Length is not (2 or 4))
    {
        return false;
    }

    path = arguments[1];
    if (arguments.Length == 2)
    {
        return true;
    }

    if (!string.Equals(
            arguments[2],
            "--idle-timeout-seconds",
            StringComparison.OrdinalIgnoreCase) ||
        !int.TryParse(
            arguments[3],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var seconds) ||
        seconds is < 1 or > 3_600)
    {
        return false;
    }

    idleTimeout = TimeSpan.FromSeconds(seconds);
    return true;
}

static async Task<int> Observe(string path, TimeSpan idleTimeout)
{
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        var observer = new DBoxSampleLogObserver(path, idleTimeout);
        var observations = 0L;
        Console.Error.WriteLine(
            "Observer boundary: one explicit local SDK sample-format log; read-only; no discovery or process access.");
        await foreach (var observation in observer.ObserveAsync(cancellation.Token))
        {
            Console.WriteLine(JsonSerializer.Serialize(observation, jsonOptions));
            observations++;
        }

        Console.Error.WriteLine(observer.Status);
        Console.Error.WriteLine($"Emitted {observations} observation record(s) as NDJSON.");
        return 0;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Observation cancelled; the source file was not changed.");
        return 130;
    }
    catch (Exception ex) when (ex is
        ArgumentException or
        IOException or
        InvalidDataException or
        NotSupportedException or
        PathTooLongException or
        UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Observer rejected the input: {ex.Message}");
        return 5;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static int Inspect(string path, bool emitFrames)
{
    string localPath;
    try
    {
        localPath = DBoxSdkLocalFilePolicy.GetValidatedExistingFilePath(path);
    }
    catch (Exception ex) when (ex is
        ArgumentException or
        FileNotFoundException or
        NotSupportedException or
        PathTooLongException or
        UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Invalid local log path: {ex.Message}");
        return 2;
    }

    var framer = new DBoxSdkXmlRecordFramer();
    var session = new DBoxSdkSampleLogSession();
    var frames = new List<StarCitizenTelemetryFrame>();

    try
    {
        using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        DBoxSdkSampleLogSession.ValidateFileLength(stream.Length);
        using var reader = new StreamReader(
            stream,
            new System.Text.UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            foreach (var xml in framer.Append(new string(buffer, 0, read)))
            {
                DBoxSdkSampleLogSession.ValidateFramingProgress(framer);
                DBoxSdkSampleLogSession.ValidateXmlRecordText(xml);
                if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var error) ||
                    record is null)
                {
                    throw new InvalidDataException(
                        $"The SDK sample log contains an invalid XML record: {error ?? "unknown parser error"}");
                }

                if (session.Process(record).Frame is { } frame)
                {
                    frames.Add(frame);
                }
            }
        }

        DBoxSdkSampleLogSession.ValidateFramingComplete(framer);
        session.ValidateComplete();
    }
    catch (Exception ex) when (ex is
        IOException or
        InvalidDataException or
        System.Text.DecoderFallbackException or
        UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Validation rejected the log: {ex.Message}");
        Console.Error.WriteLine(
            $"Inspected {session.RecordCount} record(s), emitted {frames.Count} normalized frame(s), " +
            "encountered 1 validation issue(s).");
        return ex.Message.StartsWith("Refusing AppKey", StringComparison.Ordinal) ? 3 : 4;
    }

    if (emitFrames)
    {
        foreach (var frame in frames)
        {
            Console.WriteLine(JsonSerializer.Serialize(frame));
        }
    }

    Console.Error.WriteLine(
        $"Inspected {session.RecordCount} record(s), emitted {frames.Count} normalized frame(s), " +
        "encountered 0 validation issue(s).");
    return 0;
}

static int RunSelfTest()
{
    try
    {
        TestFramingAtEverySplit();
        TestParserAndMapper();
        TestTypedProvenanceAndLifecycleBoundaries();
        TestAccelerationFallbackAndAtomicRejection();
        TestFlyerAndBoostMappings();
        TestPreviewDisplayPolicy();
        TestVisualizationSafetyGuardAsync().GetAwaiter().GetResult();
        TestStopPreservesConfiguration();
        TestReplaySourceCompletionAsync().GetAwaiter().GetResult();
        TestReplayRejectionDiagnosticsAsync().GetAwaiter().GetResult();
        TestOfficialRepeatedLifecycleSkeletons();
        TestStrictValidationParityAsync().GetAwaiter().GetResult();
        TestSampleObserverAsync().GetAwaiter().GetResult();
        TestObserverCancellationAsync().GetAwaiter().GetResult();
        TestObserverRejectionsAsync().GetAwaiter().GetResult();
        TestObserverTruncationAsync().GetAwaiter().GetResult();
        TestUnsupportedAppKey();
        TestDtdIsRejected();
        TestDuplicateCaseAttributeIsRejected();
        Console.WriteLine("D-BOX SDK sample log self-test passed.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"SELF-TEST FAILED: {ex.Message}");
        return 10;
    }
}

static void TestFramingAtEverySplit()
{
    const string record = "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" AppBuild=\"1001\" ApiKey=\"Fixture\" /></Log>";
    for (var split = 0; split <= record.Length; split++)
    {
        var framer = new DBoxSdkXmlRecordFramer();
        var first = framer.Append("junk" + record[..split]);
        var second = framer.Append(record[split..]);
        Assert(first.Count + second.Count == 1, $"Framer failed at split {split}.");
        var actual = first.Count == 1 ? first[0] : second[0];
        Assert(actual == record, $"Framer changed the record at split {split}.");
    }

    var partial = new DBoxSdkXmlRecordFramer();
    Assert(partial.Append(record[..^3]).Count == 0, "Framer emitted an incomplete record.");
}

static void TestParserAndMapper()
{
    var records = new[]
    {
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" AppBuild=\"1001\" ApiKey=\"Fixture\" /></Log>",
        "<Log TimeStamp=\"0.1\"><RegisterEvent MethodId=\"8\" Key=\"1000\" Meaning=\"1\" FieldCount=\"1\" MeaningName=\"CONFIG_UPDATE\"><Field Type=\"23\" Flags=\"0\" Meaning=\"2\" Offset=\"0\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM_MAX\" /></RegisterEvent></Log>",
        "<Log TimeStamp=\"0.2\"><PostEvent MethodId=\"9\" Key=\"1000\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"9000\" /></PostEvent></Log>",
        "<Log TimeStamp=\"0.3\"><RegisterEvent MethodId=\"8\" Key=\"1001\" Meaning=\"2\" FieldCount=\"3\" MeaningName=\"FRAME_UPDATE\"><Field Type=\"137\" Flags=\"0\" Meaning=\"68\" Offset=\"0\" TypeName=\"XyzFloat32\" MeaningName=\"ACTOR_GFORCE_XYZ\" /><Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"12\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM\" /><Field Type=\"25\" Flags=\"0\" Meaning=\"92\" Offset=\"16\" TypeName=\"Float32\" MeaningName=\"LANDING_GEAR_GENERAL_DEPLOYMENT\" /></RegisterEvent></Log>",
        "<Log TimeStamp=\"1009.5\"><PostEvent MethodId=\"9\" Key=\"1001\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0.25\" Y=\"1.25\" Z=\"-0.5\" /><Int32 Type=\"23\" Value=\"5000\" /><Float32 Type=\"25\" Value=\"0.75\" /></PostEvent></Log>",
        "<Log TimeStamp=\"1010\"><RegisterEvent MethodId=\"8\" Key=\"4000\" Meaning=\"7\" FieldCount=\"1\" MeaningName=\"IMPACT\"><Field Type=\"25\" Flags=\"0\" Meaning=\"24\" Offset=\"0\" TypeName=\"Float32\" MeaningName=\"EVENT_INTENSITY\" /></RegisterEvent></Log>",
        "<Log TimeStamp=\"1011\"><PostEvent MethodId=\"9\" Key=\"4000\" DataSize=\"4\"><Float32 Type=\"25\" Value=\"0.4\" /></PostEvent></Log>",
        "<Log TimeStamp=\"1012\"><Terminate MethodId=\"2\" /></Log>"
    };

    var previousCulture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    try
    {
        var mapper = new DBoxSdkSampleTelemetryMapper();
        var frames = new List<StarCitizenTelemetryFrame>();
        foreach (var xml in records)
        {
            Assert(
                DBoxSdkXmlLogParser.TryParse(xml, out var record, out var error) && record is not null,
                $"Parser rejected fixture XML: {error}");
            if (mapper.TryApply(record!, out var frame, out var warning) && frame is not null)
            {
                Assert(warning is null, $"Unexpected mapping warning: {warning}");
                frames.Add(frame);
            }
        }

        var flight = frames.Single(frame => frame.RawKind == "PostEvent:1001");
        AssertNear(flight.EngineRumble, 5000d / 9000d, 1e-9, "engine rumble");
        AssertNear(flight.EngineFrequencyHz, 5000d / 60d, 1e-9, "engine frequency");
        AssertNear(flight.GForceLateral, 0.25, 1e-9, "lateral G");
        AssertNear(flight.GForceVertical, 1.25, 1e-9, "vertical G");
        AssertNear(flight.GForceLongitudinal, -0.5, 1e-9, "longitudinal G");
        AssertNear(flight.LandingGear, 0.75, 1e-9, "landing gear");
        AssertDBoxProvenance(
            flight,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML replay");
        Assert(
            flight.UpdatedSignals ==
                (TelemetrySignalSet.EngineRumble |
                 TelemetrySignalSet.EngineFrequency |
                 TelemetrySignalSet.GForce |
                 TelemetrySignalSet.LandingGear),
            "Racer flight frame did not identify all typed signal updates.");
        AssertPreview(
            flight,
            "SampleRacer PostEvent:1001: engine 56% @ 83 Hz; G lat +0.25, vert +1.25, long -0.50; gear 75%");
        AssertNear(
            (flight.Timestamp - frames[0].Timestamp).TotalMilliseconds,
            1009.3,
            0.01,
            "replay timestamp delta");

        var impact = frames.Single(frame => frame.RawKind == "PostEvent:4000");
        AssertNear(impact.Impact, 0.4, 1e-9, "impact");
        AssertNear(impact.EngineRumble, flight.EngineRumble, 1e-9, "persistent engine state");
        Assert(
            impact.UpdatedSignals == TelemetrySignalSet.Impact,
            "Racer impact frame did not identify Impact as updated.");
        Assert(
            DBoxSdkSampleFramePreview.ShouldDisplay(
                impact,
                sampleFrameSequence: 1_000),
            "Racer impact was hidden by the Preview display policy.");
        AssertPreview(
            impact,
            "SampleRacer PostEvent:4000: impact 40%");

        var terminated = frames.Single(frame => frame.RawKind == "Terminate");
        Assert(!terminated.HasAnySignal, "Terminate did not emit a neutral frame.");
        Assert(
            terminated.Boundary == TelemetryFrameBoundary.Terminate,
            "Terminate did not carry its typed lifecycle boundary.");
    }
    finally
    {
        CultureInfo.CurrentCulture = previousCulture;
    }
}

static void TestTypedProvenanceAndLifecycleBoundaries()
{
    const string apiCredential = "LifecycleCredentialMustNotBeEmitted";
    var mapper = new DBoxSdkSampleTelemetryMapper();
    Apply(
        mapper,
        $"<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" AppBuild=\"1001\" ApiKey=\"{apiCredential}\" /></Log>");

    var lifecycle = new[]
    {
        (Method: "ResetState", Boundary: TelemetryFrameBoundary.ResetState),
        (Method: "Stop", Boundary: TelemetryFrameBoundary.Stop),
        (Method: "Close", Boundary: TelemetryFrameBoundary.Close),
        (Method: "Terminate", Boundary: TelemetryFrameBoundary.Terminate)
    };
    var frames = new List<StarCitizenTelemetryFrame>();
    foreach (var item in lifecycle)
    {
        var frame = Apply(
            mapper,
            $"<Log TimeStamp=\"1\"><{item.Method} /></Log>") ??
            throw new InvalidOperationException($"{item.Method} emitted no lifecycle frame.");
        frames.Add(frame);

        AssertDBoxProvenance(
            frame,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML replay");
        Assert(
            frame.Boundary == item.Boundary,
            $"{item.Method} boundary was {frame.Boundary}, expected {item.Boundary}.");
        Assert(
            frame.UpdatedSignals == TelemetrySignalSet.None,
            $"{item.Method} lifecycle frame reported updated signals.");
        Assert(!frame.HasAnySignal, $"{item.Method} lifecycle frame was not neutral.");
        Assert(
            frame.RawKind == item.Method,
            $"{item.Method} lifecycle frame lost its raw method provenance.");
        Assert(
            DBoxSdkSampleFramePreview.ShouldDisplay(frame, sampleFrameSequence: 500),
            $"{item.Method} lifecycle boundary was hidden by the Preview display policy.");
        AssertPreview(
            frame,
            $"SampleRacer {item.Method}: state neutralized");
    }

    var serialized = JsonSerializer.Serialize(frames);
    Assert(
        !serialized.Contains(apiCredential, StringComparison.Ordinal),
        "Typed lifecycle frame metadata exposed the SDK API credential.");
}

static void TestAccelerationFallbackAndAtomicRejection()
{
    var mapper = new DBoxSdkSampleTelemetryMapper();
    Apply(mapper, "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" AppBuild=\"1\" ApiKey=\"Fixture\" /></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"10\" Meaning=\"1\" FieldCount=\"1\" MeaningName=\"CONFIG_UPDATE\"><Field Type=\"23\" Flags=\"0\" Meaning=\"2\" Offset=\"0\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM_MAX\" /></RegisterEvent></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.2\"><PostEvent Key=\"10\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"8000\" /></PostEvent></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.3\"><RegisterEvent Key=\"11\" Meaning=\"2\" FieldCount=\"3\" MeaningName=\"FRAME_UPDATE\"><Field Type=\"137\" Flags=\"0\" Meaning=\"8\" Offset=\"0\" TypeName=\"XyzFloat32\" MeaningName=\"ACCELERATION_XYZ\" /><Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"12\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM\" /><Field Type=\"25\" Flags=\"0\" Meaning=\"92\" Offset=\"16\" TypeName=\"Float32\" MeaningName=\"LANDING_GEAR_GENERAL_DEPLOYMENT\" /></RegisterEvent></Log>");
    var valid = Apply(mapper, "<Log TimeStamp=\"1\"><PostEvent Key=\"11\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0.4\" Y=\"0.2\" Z=\"1.0\" /><Int32 Type=\"23\" Value=\"4000\" /><Float32 Type=\"25\" Value=\"0.5\" /></PostEvent></Log>");
    AssertNear(valid!.GForceLateral, 0.4 / 9.80665, 1e-9, "acceleration lateral G");

    var invalidXml = "<Log TimeStamp=\"2\"><PostEvent Key=\"11\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0.8\" Y=\"0.4\" Z=\"2.0\" /><Int32 Type=\"23\" Value=\"8000\" /><Float32 Type=\"25\" Value=\"NaN\" /></PostEvent></Log>";
    Assert(DBoxSdkXmlLogParser.TryParse(invalidXml, out var invalid, out _), "Invalid-value fixture did not parse structurally.");
    Assert(!mapper.TryApply(invalid!, out _, out var warning), "A non-finite known value was accepted.");
    Assert(warning is not null, "A rejected post produced no warning.");

    Apply(mapper, "<Log TimeStamp=\"2.1\"><RegisterEvent Key=\"12\" Meaning=\"3\" FieldCount=\"0\" MeaningName=\"ENGINE_START\" /></Log>");
    var afterRejected = Apply(mapper, "<Log TimeStamp=\"2.2\"><PostEvent Key=\"12\" DataSize=\"0\" /></Log>");
    AssertNear(afterRejected!.EngineRumble, 0.5, 1e-9, "atomic engine rollback");
    AssertNear(afterRejected.LandingGear, 0.5, 1e-9, "atomic gear rollback");
}

static void TestStopPreservesConfiguration()
{
    var mapper = new DBoxSdkSampleTelemetryMapper();
    Apply(mapper, "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" /></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"20\" Meaning=\"1\" FieldCount=\"1\"><Field Type=\"23\" Flags=\"0\" Meaning=\"2\" Offset=\"0\" TypeName=\"Int32\" /></RegisterEvent></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.2\"><RegisterEvent Key=\"21\" Meaning=\"2\" FieldCount=\"1\"><Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"Int32\" /></RegisterEvent></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.3\"><PostEvent Key=\"20\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"8000\" /></PostEvent></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.4\"><Stop /></Log>");
    Apply(mapper, "<Log TimeStamp=\"0.5\"><Start /></Log>");
    var resumed = Apply(mapper, "<Log TimeStamp=\"0.6\"><PostEvent Key=\"21\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"4000\" /></PostEvent></Log>");
    AssertNear(resumed!.EngineRumble, 0.5, 1e-9, "Stop/Start RPM configuration");
}

static void TestFlyerAndBoostMappings()
{
    var flyer = new DBoxSdkSampleTelemetryMapper();
    Apply(flyer, "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleFlyer\" /></Log>");
    Apply(flyer, "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"30\" Meaning=\"2\" FieldCount=\"3\" MeaningName=\"FRAME_UPDATE\"><Field Type=\"137\" Flags=\"0\" Meaning=\"68\" Offset=\"0\" TypeName=\"XyzFloat32\" MeaningName=\"ACTOR_GFORCE_XYZ\" /><Field Type=\"25\" Flags=\"0\" Meaning=\"155\" Offset=\"12\" TypeName=\"Float32\" MeaningName=\"ENGINE1_N1\" /><Field Type=\"25\" Flags=\"0\" Meaning=\"92\" Offset=\"16\" TypeName=\"Float32\" MeaningName=\"LANDING_GEAR_GENERAL_DEPLOYMENT\" /></RegisterEvent></Log>");
    var runway = Apply(flyer, "<Log TimeStamp=\"1\"><PostEvent Key=\"30\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0\" Y=\"1\" Z=\"-0.2\" /><Float32 Type=\"25\" Value=\"0.23\" /><Float32 Type=\"25\" Value=\"1\" /></PostEvent></Log>");
    AssertNear(runway!.GForceVertical, 1, 1e-9, "Flyer runway vertical G");
    AssertNear(runway.GForceLongitudinal, -0.2, 1e-9, "Flyer runway longitudinal G");
    AssertNear(runway.EngineRumble, 0.23, 1e-9, "Flyer runway N1");
    AssertNear(runway.LandingGear, 1, 1e-9, "Flyer runway gear");
    AssertDBoxProvenance(
        runway,
        "SampleFlyer",
        "D-BOX SDK SampleFlyer XML replay");
    Assert(
        runway.UpdatedSignals ==
            (TelemetrySignalSet.EngineRumble |
             TelemetrySignalSet.GForce |
             TelemetrySignalSet.LandingGear),
        "Flyer runway frame did not identify its engine/G/gear updates.");

    var cruise = Apply(flyer, "<Log TimeStamp=\"2\"><PostEvent Key=\"30\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0\" Y=\"0.5\" Z=\"-0.3\" /><Float32 Type=\"25\" Value=\"0.75\" /><Float32 Type=\"25\" Value=\"0\" /></PostEvent></Log>");
    AssertNear(cruise!.GForceVertical, 0.5, 1e-9, "Flyer cruise vertical G");
    AssertNear(cruise.EngineRumble, 0.75, 1e-9, "Flyer cruise N1");
    AssertNear(cruise.LandingGear, 0, 1e-9, "Flyer cruise gear");
    Assert(
        cruise.UpdatedSignals ==
            (TelemetrySignalSet.EngineRumble |
             TelemetrySignalSet.GForce |
             TelemetrySignalSet.LandingGear),
        "Flyer gear retraction was lost because its resulting value was zero.");
    Assert(
        DBoxSdkSampleFramePreview.ShouldDisplay(cruise, sampleFrameSequence: 1_000),
        "Flyer gear retraction was hidden by the Preview display policy.");

    var racer = new DBoxSdkSampleTelemetryMapper();
    Apply(racer, "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" /></Log>");
    Apply(racer, "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"40\" Meaning=\"5\" FieldCount=\"1\"><Field Type=\"25\" Flags=\"0\" Meaning=\"24\" Offset=\"0\" TypeName=\"Float32\" /></RegisterEvent></Log>");
    Apply(racer, "<Log TimeStamp=\"0.2\"><RegisterEvent Key=\"41\" Meaning=\"6\" FieldCount=\"0\" /></Log>");
    var boostStart = Apply(racer, "<Log TimeStamp=\"1\"><PostEvent Key=\"40\" DataSize=\"4\"><Float32 Type=\"25\" Value=\"0.75\" /></PostEvent></Log>");
    AssertNear(boostStart!.Boost, 0.75, 1e-9, "Racer boost start");
    Assert(
        boostStart.UpdatedSignals == TelemetrySignalSet.Boost,
        "Racer boost start did not identify Boost as updated.");
    var boostStop = Apply(racer, "<Log TimeStamp=\"2\"><PostEvent Key=\"41\" DataSize=\"0\" /></Log>");
    AssertNear(boostStop!.Boost, 0, 1e-9, "Racer boost stop");
    AssertNear(boostStop.Afterburner, 0, 1e-9, "Racer boost is not afterburner");
    Assert(
        boostStop.UpdatedSignals == TelemetrySignalSet.Boost,
        "Racer boost-off transition was lost because its resulting value was zero.");
    Assert(
        DBoxSdkSampleFramePreview.ShouldDisplay(boostStop, sampleFrameSequence: 1_000),
        "Racer boost-off transition was hidden by the Preview display policy.");

    var previousCulture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    try
    {
        AssertPreview(
            runway,
            "SampleFlyer PostEvent:30: engine 23%; G lat 0.00, vert +1.00, long -0.20; gear deployed");
        AssertPreview(
            cruise,
            "SampleFlyer PostEvent:30: engine 75%; G lat 0.00, vert +0.50, long -0.30; gear retracted");
        AssertPreview(
            boostStart,
            "SampleRacer PostEvent:40: boost 75%");
        AssertPreview(
            boostStop,
            "SampleRacer PostEvent:41: boost off");
    }
    finally
    {
        CultureInfo.CurrentCulture = previousCulture;
    }
}

static void TestPreviewDisplayPolicy()
{
    var periodicFrame = new StarCitizenTelemetryFrame
    {
        Source = "D-BOX SDK SampleRacer XML replay",
        SourceKind = TelemetrySourceKind.DBoxSdkSample,
        ApplicationKey = "SampleRacer",
        UpdatedSignals = TelemetrySignalSet.GForce,
        GForceLateral = 0.08,
        GForceVertical = 0.01,
        GForceLongitudinal = 0.28,
        RawKind = "PostEvent:1001"
    };

    Assert(
        DBoxSdkSampleFramePreview.ShouldDisplay(periodicFrame, sampleFrameSequence: 12),
        "Preview policy hid one of the first twelve sample frames.");
    Assert(
        !DBoxSdkSampleFramePreview.ShouldDisplay(periodicFrame, sampleFrameSequence: 13),
        "Preview policy failed to throttle a steady sample frame.");
    Assert(
        DBoxSdkSampleFramePreview.ShouldDisplay(periodicFrame, sampleFrameSequence: 30),
        "Preview policy hid the periodic thirtieth sample frame.");

    var nonSample = periodicFrame with
    {
        Source = "Unrelated telemetry",
        SourceKind = TelemetrySourceKind.Unknown,
        ApplicationKey = null
    };
    Assert(
        !DBoxSdkSampleFramePreview.ShouldDisplay(nonSample, sampleFrameSequence: 1),
        "Preview policy accepted a non-D-BOX-sample frame.");
    Assert(
        !DBoxSdkSampleFramePreview.TryFormat(nonSample, out _),
        "Preview formatter accepted a non-D-BOX-sample frame.");

    var replayComplete = periodicFrame with
    {
        UpdatedSignals = TelemetrySignalSet.None,
        GForceLateral = 0,
        GForceVertical = 0,
        GForceLongitudinal = 0,
        Boundary = TelemetryFrameBoundary.ReplayComplete,
        RawKind = "ReplayComplete"
    };
    Assert(
        !DBoxSdkSampleFramePreview.ShouldDisplay(replayComplete, sampleFrameSequence: 1),
        "Preview policy emitted a duplicate ReplayComplete feed entry.");
    Assert(
        !DBoxSdkSampleFramePreview.TryFormat(replayComplete, out _),
        "Preview formatter emitted a duplicate ReplayComplete summary.");
}

static async Task TestVisualizationSafetyGuardAsync()
{
    await using var noSource = new NoTelemetrySource(
        TelemetryOutputPolicy.VisualizationOnly);
    Assert(
        noSource.OutputPolicy == TelemetryOutputPolicy.VisualizationOnly,
        "A missing visualization-only source failed open to hardware effects.");

    var device = new RecordingForceFeedbackDevice();
    var controller = new ForceFeedbackController(device);
    await controller.InitializeAsync(CancellationToken.None);
    var blocked = await controller.HandleTelemetryAsync(
        new StarCitizenTelemetryFrame
        {
            Source = "Adversarial sample fixture",
            SourceKind = TelemetrySourceKind.DBoxSdkSample,
            ApplicationKey = "SampleRacer",
            Afterburner = 1,
            Atmosphere = 1
        },
        CancellationToken.None);

    Assert(
        blocked.Contains("blocked", StringComparison.OrdinalIgnoreCase),
        "The controller did not report its D-BOX sample safety rejection.");
    Assert(
        device.PlayCount == 0 && device.StopCount == 0,
        "A D-BOX sample frame reached the force-feedback device.");

    await controller.HandleTelemetryAsync(
        new StarCitizenTelemetryFrame
        {
            Source = "Ordinary telemetry fixture",
            Afterburner = 1
        },
        CancellationToken.None);
    Assert(
        device.PlayCount == 1,
        "The safety test device did not detect an ordinary allowed force update.");
}

static async Task TestReplaySourceCompletionAsync()
{
    var path = Path.Combine(
        Path.GetTempPath(),
        $"moza-dbox-sanitized-{Guid.NewGuid():N}.log");
    const string apiCredential = "ReplayCredentialMustRemainSecret";
    var fixture = CreateStrictSampleRacerFixture(apiCredential);

    try
    {
        await File.WriteAllTextAsync(
            path,
            fixture,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using var source = new DBoxSdkSampleLogTelemetrySource(path, replaySpeed: 0);
        Assert(
            source.OutputPolicy == TelemetryOutputPolicy.VisualizationOnly,
            "D-BOX SDK sample replay did not enforce VisualizationOnly output.");
        var frames = new List<StarCitizenTelemetryFrame>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var frame in source.ReadFramesAsync(cancellation.Token))
        {
            frames.Add(frame);
        }

        Assert(
            !cancellation.IsCancellationRequested,
            "Replay source did not complete naturally.");
        var post = frames.Single(frame => frame.RawKind == "PostEvent:50");
        AssertDBoxProvenance(
            post,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML replay");
        Assert(
            post.UpdatedSignals ==
                (TelemetrySignalSet.EngineRumble |
                 TelemetrySignalSet.EngineFrequency),
            "Replay post did not retain its typed engine update metadata.");

        var terminated = frames.Single(frame =>
            frame.Boundary == TelemetryFrameBoundary.Terminate);
        AssertDBoxProvenance(
            terminated,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML replay");
        Assert(
            terminated.UpdatedSignals == TelemetrySignalSet.None &&
            !terminated.HasAnySignal,
            "Replay Terminate boundary was not a typed neutral frame.");

        var completion = frames[^1];
        Assert(
            completion.RawKind == "ReplayComplete",
            "Replay source emitted no completion marker.");
        AssertDBoxProvenance(
            completion,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML replay");
        Assert(
            completion.Boundary == TelemetryFrameBoundary.ReplayComplete,
            "Replay completion marker did not carry the ReplayComplete boundary.");
        Assert(
            completion.UpdatedSignals == TelemetrySignalSet.None,
            "Replay completion marker reported updated signals.");
        Assert(!completion.HasAnySignal, "Replay completion frame was not neutral.");
        Assert(
            !DBoxSdkSampleFramePreview.ShouldDisplay(
                completion,
                sampleFrameSequence: frames.Count),
            "ReplayComplete was not suppressed from the per-frame Preview feed.");
        Assert(
            !DBoxSdkSampleFramePreview.TryFormat(completion, out _),
            "ReplayComplete produced a duplicate per-frame Preview summary.");

        var diagnostics = await source.GetDiagnosticsAsync(CancellationToken.None);
        var diagnosticsText = string.Join(Environment.NewLine, diagnostics);
        Assert(
            diagnosticsText.Contains("(present; redacted)", StringComparison.Ordinal),
            "Replay diagnostics did not report the API credential as redacted.");
        Assert(
            !diagnosticsText.Contains(apiCredential, StringComparison.Ordinal),
            "Replay diagnostics exposed the SDK API credential.");
        Assert(source.Status.StartsWith("Replay complete:", StringComparison.Ordinal), "Replay source did not report completion.");
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task TestReplayRejectionDiagnosticsAsync()
{
    var invalidUtf8Path = CreateObserverFixturePath();
    try
    {
        await File.WriteAllBytesAsync(invalidUtf8Path, [0xff]);
        await AssertReplayDiagnosticRejectionAsync(
            invalidUtf8Path,
            exception => exception is System.Text.DecoderFallbackException,
            "invalid UTF-8");
    }
    finally
    {
        File.Delete(invalidUtf8Path);
    }

    var oversizedPath = CreateObserverFixturePath();
    try
    {
        await using (var stream = new FileStream(
                         oversizedPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(DBoxSdkSampleLogSession.MaximumLogBytes + 1);
        }

        await AssertReplayDiagnosticRejectionAsync(
            oversizedPath,
            exception => exception is InvalidDataException,
            "oversized input");
    }
    finally
    {
        File.Delete(oversizedPath);
    }

    var directoryPath = CreateObserverFixturePath();
    try
    {
        Directory.CreateDirectory(directoryPath);
        await AssertReplayDiagnosticRejectionAsync(
            directoryPath,
            exception => exception is ArgumentException,
            "directory input");
    }
    finally
    {
        Directory.Delete(directoryPath);
    }
}

static async Task AssertReplayDiagnosticRejectionAsync(
    string path,
    Func<Exception, bool> isExpectedException,
    string label)
{
    await using var source = new DBoxSdkSampleLogTelemetrySource(
        path,
        replaySpeed: 0);
    Exception? rejection = null;
    var yieldedFrames = 0;
    try
    {
        await foreach (var _ in source.ReadFramesAsync(CancellationToken.None))
        {
            yieldedFrames++;
        }
    }
    catch (Exception ex) when (ex is
        InvalidDataException or
        IOException or
        ArgumentException or
        NotSupportedException)
    {
        rejection = ex;
    }

    Assert(
        rejection is not null && isExpectedException(rejection),
        $"Replay did not reject {label} with the expected exception category.");
    Assert(yieldedFrames == 0, $"Replay yielded frames from {label}.");
    Assert(
        source.Status.StartsWith("Replay rejected:", StringComparison.Ordinal),
        $"Replay status did not report rejection for {label}.");

    var diagnostics = string.Join(
        Environment.NewLine,
        await source.GetDiagnosticsAsync(CancellationToken.None));
    Assert(
        diagnostics.Contains(
            "Records/frames/validation failures: 0/0/1",
            StringComparison.Ordinal),
        $"Replay diagnostics did not count the {label} rejection.");
    Assert(
        !diagnostics.Contains("Validation warning: (none)", StringComparison.Ordinal),
        $"Replay diagnostics lost the {label} rejection message.");
}

static async Task TestStrictValidationParityAsync()
{
    var canonicalRecords = CreateStrictSampleRacerRecords("StrictParityCredential");
    var canonicalFixture = JoinStrictFixture(canonicalRecords);

    var wrongMethodId = canonicalRecords.ToArray();
    wrongMethodId[5] = wrongMethodId[5].Replace(
        "MethodId=\"9\"",
        "MethodId=\"8\"",
        StringComparison.Ordinal);

    var missingMethodId = canonicalRecords.ToArray();
    missingMethodId[5] = missingMethodId[5].Replace(
        " MethodId=\"9\"",
        string.Empty,
        StringComparison.Ordinal);

    var postBeforeOpen = new[]
    {
        canonicalRecords[0],
        canonicalRecords[1],
        canonicalRecords[5].Replace(
            "TimeStamp=\"0.5\"",
            "TimeStamp=\"0.15\"",
            StringComparison.Ordinal),
        canonicalRecords[2],
        canonicalRecords[3],
        canonicalRecords[4],
        canonicalRecords[6],
        canonicalRecords[7],
        canonicalRecords[8]
    };

    var timestampRegression = canonicalRecords.ToArray();
    timestampRegression[5] = timestampRegression[5].Replace(
        "TimeStamp=\"0.5\"",
        "TimeStamp=\"0.35\"",
        StringComparison.Ordinal);

    var recordAfterTerminate = canonicalRecords
        .Append(
            "<Log TimeStamp=\"0.9\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\">" +
            "<Int32 Type=\"23\" Value=\"3100\" /></PostEvent></Log>")
        .ToArray();

    var cases = new (string Name, string Fixture, string Category)[]
    {
        (
            "wrong MethodId",
            JoinStrictFixture(wrongMethodId),
            "must carry MethodId 9"),
        (
            "missing MethodId",
            JoinStrictFixture(missingMethodId),
            "must carry MethodId 9"),
        (
            "Post before Open",
            JoinStrictFixture(postBeforeOpen),
            "requires an open run"),
        (
            "timestamp regression",
            JoinStrictFixture(timestampRegression),
            "timestamp regression"),
        (
            "record after Terminate",
            JoinStrictFixture(recordAfterTerminate),
            "record after Terminate"),
        (
            "missing Terminate",
            JoinStrictFixture(canonicalRecords[..^1]),
            "Terminate"),
        (
            "no schema/post/frame",
            JoinStrictFixture([canonicalRecords[0], canonicalRecords[8]]),
            "no complete schema/post/frame sequence")
    };

    ValidateFixtureWithSession(canonicalFixture);
    var positivePath = CreateObserverFixturePath();
    try
    {
        await File.WriteAllTextAsync(positivePath, canonicalFixture);

        var (inspectExitCode, inspectError) = InvokeInspect(positivePath);
        Assert(
            inspectExitCode == 0,
            $"Inspector rejected the canonical strict fixture: {inspectError}");

        await using (var source = new DBoxSdkSampleLogTelemetrySource(
                         positivePath,
                         replaySpeed: 0))
        {
            var replayFrames = new List<StarCitizenTelemetryFrame>();
            await foreach (var frame in source.ReadFramesAsync(CancellationToken.None))
            {
                replayFrames.Add(frame);
                if (replayFrames.Count == 1)
                {
                    AssertReplayInputRemainsLocked(positivePath);
                }
            }

            Assert(
                replayFrames.Count > 1 &&
                replayFrames[^1].Boundary == TelemetryFrameBoundary.ReplayComplete,
                "Replay source rejected or incompletely replayed the canonical strict fixture.");
        }

        var observer = new DBoxSampleLogObserver(
            positivePath,
            TimeSpan.FromSeconds(1));
        var observationCount = 0;
        await foreach (var _ in observer.ObserveAsync(CancellationToken.None))
        {
            observationCount++;
        }

        Assert(
            observationCount == canonicalRecords.Length &&
            observer.Status.StartsWith("Observation complete:", StringComparison.Ordinal),
            "Observer rejected or incompletely observed the canonical strict fixture.");
    }
    finally
    {
        File.Delete(positivePath);
    }

    foreach (var validationCase in cases)
    {
        await AssertStrictValidationParityRejectsAsync(
            validationCase.Name,
            validationCase.Fixture,
            validationCase.Category);
    }
}

static void TestOfficialRepeatedLifecycleSkeletons()
{
    foreach (var appKey in new[] { "SampleRacer", "SampleFlyer" })
    {
        var fixture = string.Join(
            Environment.NewLine,
            $"<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"{appKey}\" /></Log>",
            "<Log TimeStamp=\"0.1\"><RegisterEvent MethodId=\"8\" Key=\"50\" Meaning=\"2\" FieldCount=\"1\">" +
            "<Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM\" />" +
            "</RegisterEvent></Log>",
            "<Log TimeStamp=\"0.2\"><Open MethodId=\"3\" /></Log>",
            "<Log TimeStamp=\"0.3\"><ResetState MethodId=\"7\" /></Log>",
            "<Log TimeStamp=\"0.4\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"1000\" /></PostEvent></Log>",
            "<Log TimeStamp=\"0.5\"><Start MethodId=\"5\" /></Log>",
            "<Log TimeStamp=\"0.6\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"2000\" /></PostEvent></Log>",
            "<Log TimeStamp=\"0.7\"><Stop MethodId=\"6\" /></Log>",
            "<Log TimeStamp=\"0.8\"><Start MethodId=\"5\" /></Log>",
            "<Log TimeStamp=\"0.9\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"3000\" /></PostEvent></Log>",
            "<Log TimeStamp=\"1.0\"><Stop MethodId=\"6\" /></Log>",
            "<Log TimeStamp=\"1.1\"><Close MethodId=\"4\" /></Log>",
            "<Log TimeStamp=\"1.2\"><Terminate MethodId=\"2\" /></Log>") +
            Environment.NewLine;

        var session = ValidateFixtureWithSession(fixture);
        Assert(
            session.RecordCount == 13 && session.FrameCount == 8,
            $"{appKey} repeated Start/Stop lifecycle skeleton was not fully accepted.");
    }
}

static void AssertReplayInputRemainsLocked(string path)
{
    var writeRejected = false;
    try
    {
        using var _ = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        writeRejected = true;
    }

    var deleteRejected = false;
    try
    {
        File.Delete(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        deleteRejected = true;
    }

    Assert(
        writeRejected && deleteRejected,
        "Replay released its validated input handle before playback completed.");
}

static async Task AssertStrictValidationParityRejectsAsync(
    string name,
    string fixture,
    string expectedCategory)
{
    AssertInvalidDataCategory(
        $"session ({name})",
        expectedCategory,
        () => ValidateFixtureWithSession(fixture));

    var path = CreateObserverFixturePath();
    try
    {
        await File.WriteAllTextAsync(path, fixture);

        var (inspectExitCode, inspectError) = InvokeInspect(path);
        Assert(
            inspectExitCode != 0 &&
            inspectError.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase),
            $"Inspector ({name}) did not reject with category '{expectedCategory}'. " +
            $"Exit code: {inspectExitCode}; stderr: {inspectError}");

        var replayYieldCount = 0;
        await AssertInvalidDataCategoryAsync(
            $"replay source ({name})",
            expectedCategory,
            async () =>
            {
                await using var source = new DBoxSdkSampleLogTelemetrySource(
                    path,
                    replaySpeed: 0);
                await foreach (var _ in source.ReadFramesAsync(CancellationToken.None))
                {
                    replayYieldCount++;
                }
            });
        Assert(
            replayYieldCount == 0,
            $"Replay source ({name}) yielded {replayYieldCount} frame(s) before rejection.");

        await AssertInvalidDataCategoryAsync(
            $"observer ({name})",
            expectedCategory,
            async () =>
            {
                var observer = new DBoxSampleLogObserver(
                    path,
                    TimeSpan.FromMilliseconds(500));
                await foreach (var _ in observer.ObserveAsync(CancellationToken.None))
                {
                }
            });
    }
    finally
    {
        File.Delete(path);
    }
}

static DBoxSdkSampleLogSession ValidateFixtureWithSession(string fixture)
{
    var framer = new DBoxSdkXmlRecordFramer();
    var session = new DBoxSdkSampleLogSession();
    foreach (var xml in framer.Append(fixture))
    {
        DBoxSdkSampleLogSession.ValidateFramingProgress(framer);
        DBoxSdkSampleLogSession.ValidateXmlRecordText(xml);
        if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var error) ||
            record is null)
        {
            throw new InvalidDataException(
                $"The SDK sample log contains an invalid XML record: {error ?? "unknown parser error"}");
        }

        session.Process(record);
    }

    DBoxSdkSampleLogSession.ValidateFramingComplete(framer);
    session.ValidateComplete();
    return session;
}

static (int ExitCode, string StandardError) InvokeInspect(string path)
{
    var originalError = Console.Error;
    using var capturedError = new StringWriter(CultureInfo.InvariantCulture);
    try
    {
        Console.SetError(capturedError);
        var exitCode = Inspect(path, emitFrames: false);
        return (exitCode, capturedError.ToString());
    }
    finally
    {
        Console.SetError(originalError);
    }
}

static void AssertInvalidDataCategory(
    string surface,
    string expectedCategory,
    Action action)
{
    try
    {
        action();
    }
    catch (InvalidDataException ex)
    {
        Assert(
            ex.Message.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase),
            $"{surface} rejected outside category '{expectedCategory}': {ex.Message}");
        return;
    }

    throw new InvalidOperationException(
        $"{surface} accepted a fixture expected to fail as '{expectedCategory}'.");
}

static async Task AssertInvalidDataCategoryAsync(
    string surface,
    string expectedCategory,
    Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidDataException ex)
    {
        Assert(
            ex.Message.Contains(expectedCategory, StringComparison.OrdinalIgnoreCase),
            $"{surface} rejected outside category '{expectedCategory}': {ex.Message}");
        return;
    }

    throw new InvalidOperationException(
        $"{surface} accepted a fixture expected to fail as '{expectedCategory}'.");
}

static string CreateStrictSampleRacerFixture(string apiCredential) =>
    JoinStrictFixture(CreateStrictSampleRacerRecords(apiCredential));

static string[] CreateStrictSampleRacerRecords(string apiCredential) =>
[
    "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" AppBuild=\"1001\" ApiKey=\"" +
    apiCredential +
    "\" /></Log>",
    "<Log TimeStamp=\"0.1\"><RegisterEvent MethodId=\"8\" Key=\"50\" Meaning=\"2\" FieldCount=\"1\" MeaningName=\"FRAME_UPDATE\">" +
    "<Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM\" />" +
    "</RegisterEvent></Log>",
    "<Log TimeStamp=\"0.2\"><Open MethodId=\"3\" /></Log>",
    "<Log TimeStamp=\"0.3\"><ResetState MethodId=\"7\" /></Log>",
    "<Log TimeStamp=\"0.4\"><Start MethodId=\"5\" /></Log>",
    "<Log TimeStamp=\"0.5\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\">" +
    "<Int32 Type=\"23\" Value=\"3000\" /></PostEvent></Log>",
    "<Log TimeStamp=\"0.6\"><Stop MethodId=\"6\" /></Log>",
    "<Log TimeStamp=\"0.7\"><Close MethodId=\"4\" /></Log>",
    "<Log TimeStamp=\"0.8\"><Terminate MethodId=\"2\" /></Log>"
];

static string JoinStrictFixture(IEnumerable<string> records) =>
    string.Join(Environment.NewLine, records) + Environment.NewLine;

static async Task TestSampleObserverAsync()
{
    var path = CreateObserverFixturePath();
    var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    const string prefix =
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" AppBuild=\"1001\" ApiKey=\"MustNotBeEmitted\" /></Log>\n" +
        "<Log TimeStamp=\"0.1\"><RegisterEvent MethodId=\"8\" Key=\"50\" Meaning=\"2\" FieldCount=\"1\" MeaningName=\"FRAME_UPDATE\"><Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"Int32\" MeaningName=\"ENGINE_RPM\" /></RegisterEvent></Log>\n" +
        "<Log TimeStamp=\"0.2\"><Open MethodId=\"3\" /></Log>\n";
    const string post =
        "<Log TimeStamp=\"1.2\"><PostEvent MethodId=\"9\" Key=\"50\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"3000\" /></PostEvent></Log>\n";
    const string suffix =
        "<Log TimeStamp=\"1.3\"><Close MethodId=\"4\" /></Log>\n" +
        "<Log TimeStamp=\"1.4\"><Terminate MethodId=\"2\" /></Log>\n";

    try
    {
        await File.WriteAllTextAsync(path, string.Empty, encoding);
        var observer = new DBoxSampleLogObserver(path, TimeSpan.FromSeconds(3));
        var writer = Task.Run(async () =>
        {
            await Task.Delay(75);
            await File.AppendAllTextAsync(path, prefix, encoding);
            await Task.Delay(100);
            await File.AppendAllTextAsync(path, post[..(post.Length / 2)], encoding);
            await Task.Delay(125);
            await File.AppendAllTextAsync(path, post[(post.Length / 2)..] + suffix, encoding);
        });

        var observations = new List<DBoxSampleObservation>();
        await foreach (var observation in observer.ObserveAsync(CancellationToken.None))
        {
            observations.Add(observation);
        }

        await writer;
        Assert(observations.Count == 6, "Observer did not emit exactly one item per complete XML record.");
        Assert(
            observations.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, 6).Select(value => (long)value)),
            "Observer sequence numbers were not contiguous.");
        var postObservation = observations.Single(item => item.Method == "PostEvent");
        var normalizedFrame = postObservation.NormalizedFrame ??
            throw new InvalidOperationException("Observer did not map the completed post.");
        Assert(
            postObservation.AppKey == "SampleRacer" &&
            postObservation.AppBuild == 1001,
            "Observer did not retain typed sample application metadata.");
        AssertDBoxProvenance(
            normalizedFrame,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML observer");
        Assert(
            normalizedFrame.UpdatedSignals ==
                (TelemetrySignalSet.EngineRumble |
                 TelemetrySignalSet.EngineFrequency),
            "Observer normalized frame did not retain typed update metadata.");
        Assert(
            normalizedFrame.Boundary == TelemetryFrameBoundary.None,
            "Observer post was incorrectly labeled as a lifecycle boundary.");

        var closeFrame = observations
            .Single(item => item.Method == "Close")
            .NormalizedFrame ??
            throw new InvalidOperationException("Observer did not map Close.");
        var terminateFrame = observations
            .Single(item => item.Method == "Terminate")
            .NormalizedFrame ??
            throw new InvalidOperationException("Observer did not map Terminate.");
        Assert(
            closeFrame.Boundary == TelemetryFrameBoundary.Close &&
            terminateFrame.Boundary == TelemetryFrameBoundary.Terminate,
            "Observer lifecycle frames lost their typed boundaries.");
        AssertDBoxProvenance(
            closeFrame,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML observer");
        AssertDBoxProvenance(
            terminateFrame,
            "SampleRacer",
            "D-BOX SDK SampleRacer XML observer");
        Assert(
            !JsonSerializer.Serialize(observations).Contains("MustNotBeEmitted", StringComparison.Ordinal),
            "Observer output exposed the SDK ApiKey.");
        Assert(
            observer.Status.StartsWith("Observation complete:", StringComparison.Ordinal),
            "Observer did not report clean completion.");
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task TestObserverCancellationAsync()
{
    var path = CreateObserverFixturePath();
    try
    {
        await File.WriteAllTextAsync(path, string.Empty);
        var observer = new DBoxSampleLogObserver(path, TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var cancelled = false;
        try
        {
            await foreach (var _ in observer.ObserveAsync(cancellation.Token))
            {
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "Observer did not honor cancellation while waiting at EOF.");
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task TestObserverRejectionsAsync()
{
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"UnapprovedGame\" /></Log>",
        "Only SampleRacer and SampleFlyer");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"1\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>",
        "exactly one Initialize");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"1\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"0.5\"><Terminate MethodId=\"2\" /></Log>",
        "timestamp regression");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"1\"><Terminate MethodId=\"2\" /></Log>" +
        "<Log TimeStamp=\"2\"><PostEvent MethodId=\"9\" Key=\"1\" DataSize=\"0\" /></Log>",
        "record after Terminate");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"1\"><UnexpectedMethod MethodId=\"99\" /></Log>",
        "outside the SDK sample allowlist");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"9\" AppKey=\"SampleRacer\" /></Log>",
        "must carry MethodId 1");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\"><!--ignored--></Initialize></Log>",
        "Comments and processing instructions");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"1\"><PostEvent MethodId=\"9\" Key=\"1\" DataSize=\"0\" /></Log>",
        "requires an open run");
    await AssertObserverRejectsAsync(
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>" +
        "<Log TimeStamp=\"1\"><RegisterEvent MethodId=\"8\" Key=\"1\" Meaning=\"2\" FieldCount=\"1\">" +
        "<Field Type=\"137\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"XyzFloat32\" />" +
        "</RegisterEvent></Log>",
        "failed schema or payload validation");
}

static async Task TestObserverTruncationAsync()
{
    var path = CreateObserverFixturePath();
    const string initialize =
        "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"SampleRacer\" /></Log>";
    try
    {
        await File.WriteAllTextAsync(path, initialize);
        var observer = new DBoxSampleLogObserver(path, TimeSpan.FromSeconds(3));
        await using var enumerator = observer
            .ObserveAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        Assert(await enumerator.MoveNextAsync(), "Observer did not read the truncation fixture.");

        await using (var writer = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.ReadWrite))
        {
            writer.SetLength(0);
            await writer.FlushAsync();
        }

        var rejected = false;
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase);
        }

        Assert(rejected, "Observer did not reject truncation of its held file.");
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task AssertObserverRejectsAsync(string fixture, string expectedMessage)
{
    var path = CreateObserverFixturePath();
    try
    {
        await File.WriteAllTextAsync(path, fixture);
        var observer = new DBoxSampleLogObserver(path, TimeSpan.FromSeconds(1));
        var rejected = false;
        try
        {
            await foreach (var _ in observer.ObserveAsync(CancellationToken.None))
            {
            }
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase);
        }

        Assert(rejected, $"Observer did not reject fixture with: {expectedMessage}.");
    }
    finally
    {
        File.Delete(path);
    }
}

static string CreateObserverFixturePath() =>
    Path.Combine(Path.GetTempPath(), $"moza-dbox-observer-{Guid.NewGuid():N}.log");

static void TestUnsupportedAppKey()
{
    const string xml = "<Log TimeStamp=\"0\"><Initialize MethodId=\"1\" AppKey=\"UnapprovedGame\" AppBuild=\"1\" ApiKey=\"Fixture\" /></Log>";
    Assert(DBoxSdkXmlLogParser.TryParse(xml, out var record, out _), "Unsupported-key fixture did not parse.");
    var mapper = new DBoxSdkSampleTelemetryMapper();
    mapper.TryApply(record!, out _, out var warning);
    Assert(!mapper.IsSupportedSample, "Unsupported AppKey was accepted.");
    Assert(warning is not null, "Unsupported AppKey did not produce a warning.");
}

static void TestDtdIsRejected()
{
    const string xml = "<!DOCTYPE Log [<!ENTITY xxe SYSTEM \"file:///nope\">]><Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" /></Log>";
    Assert(!DBoxSdkXmlLogParser.TryParse(xml, out _, out _), "DTD-bearing XML was accepted.");
}

static void TestDuplicateCaseAttributeIsRejected()
{
    const string xml = "<Log TimeStamp=\"0\"><PostEvent Key=\"1\" DataSize=\"4\"><Float32 Type=\"25\" Value=\"1\" value=\"2\" /></PostEvent></Log>";
    Assert(!DBoxSdkXmlLogParser.TryParse(xml, out _, out _), "Duplicate case-insensitive attributes were accepted.");
}

static StarCitizenTelemetryFrame? Apply(DBoxSdkSampleTelemetryMapper mapper, string xml)
{
    Assert(DBoxSdkXmlLogParser.TryParse(xml, out var record, out var error), $"Fixture parse failed: {error}");
    var mapped = mapper.TryApply(record!, out var frame, out var warning);
    Assert(warning is null, $"Fixture mapping warning: {warning}");
    return mapped ? frame : null;
}

static void AssertDBoxProvenance(
    StarCitizenTelemetryFrame frame,
    string expectedApplicationKey,
    string expectedSource)
{
    Assert(
        frame.SourceKind == TelemetrySourceKind.DBoxSdkSample,
        $"Frame source kind was {frame.SourceKind}, expected DBoxSdkSample.");
    Assert(
        frame.ApplicationKey == expectedApplicationKey,
        $"Frame AppKey was '{frame.ApplicationKey ?? "(missing)"}', expected '{expectedApplicationKey}'.");
    Assert(
        frame.Source == expectedSource,
        $"Frame source was '{frame.Source}', expected '{expectedSource}'.");
}

static void AssertPreview(
    StarCitizenTelemetryFrame frame,
    string expected)
{
    Assert(
        DBoxSdkSampleFramePreview.TryFormat(frame, out var actual),
        $"Preview formatter rejected {frame.ApplicationKey ?? "(unknown)"} {frame.RawKind ?? "frame"}.");
    Assert(
        actual == expected,
        $"Preview mismatch. Expected '{expected}', got '{actual}'.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertNear(double actual, double expected, double tolerance, string label) =>
    Assert(Math.Abs(actual - expected) <= tolerance, $"{label}: expected {expected}, got {actual}.");

static void PrintUsage()
{
    Console.WriteLine("DBoxLogInspect");
    Console.WriteLine("  --self-test         run the sanitized offline parser/mapper checks");
    Console.WriteLine("  --validate <path>   validate a local sample log without printing frames");
    Console.WriteLine("  --observe <absolute-path> [--idle-timeout-seconds 1..3600]");
    Console.WriteLine("                      follow one existing local SDK sample log as NDJSON");
    Console.WriteLine("  <sample-log-path>   print normalized frames from SampleRacer or SampleFlyer XML");
}

file sealed class RecordingForceFeedbackDevice : IForceFeedbackDevice
{
    public string Name => "Recording test output";

    public string Status => "Self-test only.";

    public int PlayCount { get; private set; }

    public int StopCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PrepareAsync(
        IEnumerable<ForceEffect> effects,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PlayAsync(ForceEffect effect, CancellationToken cancellationToken)
    {
        PlayCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync(string stateKey, CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
