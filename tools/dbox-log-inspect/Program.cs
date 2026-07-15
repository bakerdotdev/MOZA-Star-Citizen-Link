using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        localPath = DBoxSdkLocalFilePolicy.GetValidatedPath(path);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        Console.Error.WriteLine($"Invalid local log path: {ex.Message}");
        return 2;
    }

    if (!File.Exists(localPath))
    {
        Console.Error.WriteLine($"Log file not found: {localPath}");
        return 2;
    }

    var framer = new DBoxSdkXmlRecordFramer();
    var mapper = new DBoxSdkSampleTelemetryMapper();
    var records = 0;
    var frames = 0;
    var failures = 0;
    var initializeCount = 0;

    using var reader = new StreamReader(
        localPath,
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: true,
        bufferSize: 4096);
    var buffer = new char[4096];
    while (true)
    {
        var read = reader.Read(buffer, 0, buffer.Length);
        if (read == 0)
        {
            break;
        }

        foreach (var xml in framer.Append(new string(buffer, 0, read)))
        {
            if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var error) ||
                record is null)
            {
                failures++;
                Console.Error.WriteLine($"Parse warning: {error}");
                continue;
            }

            records++;
            if (records == 1 && record.Method != "Initialize")
            {
                failures++;
                Console.Error.WriteLine("Validation warning: Initialize is not the first record.");
            }

            if (record.Method == "Initialize")
            {
                initializeCount++;
                if (initializeCount > 1)
                {
                    failures++;
                    Console.Error.WriteLine("Validation warning: the log contains multiple Initialize records.");
                }
            }

            var validationFailuresBefore = mapper.ValidationFailureCount;
            var mapped = mapper.TryApply(record, out var frame, out var warning);
            if (warning is not null)
            {
                failures++;
                Console.Error.WriteLine($"Mapping warning: {warning}");
            }

            if (mapper.ValidationFailureCount > validationFailuresBefore && warning is null)
            {
                failures++;
                Console.Error.WriteLine("Mapping warning: a schema or payload validation failed.");
            }

            if (record.Method == "Initialize" && !mapper.IsSupportedSample)
            {
                Console.Error.WriteLine(
                    $"Refusing AppKey '{mapper.AppKey ?? "(missing)"}'. " +
                    "This offline tool accepts only SampleRacer and SampleFlyer logs.");
                return 3;
            }

            if (mapped && frame is not null)
            {
                frames++;
                if (emitFrames)
                {
                    Console.WriteLine(JsonSerializer.Serialize(frame));
                }
            }
        }
    }

    if (framer.DiscardedNonWhitespaceCharacters > 0 ||
        framer.HasNonWhitespaceBufferedContent)
    {
        failures++;
        Console.Error.WriteLine(
            "Validation warning: the log contains discarded text or an incomplete final XML record.");
    }

    if (initializeCount != 1 || !mapper.IsSupportedSample)
    {
        failures++;
        Console.Error.WriteLine("Validation warning: exactly one supported Initialize record is required.");
    }

    if (mapper.RegisteredSchemaCount == 0 || mapper.PostCount == 0 || frames == 0)
    {
        failures++;
        Console.Error.WriteLine("Validation warning: the log has no complete schema/post/frame sequence.");
    }

    if (!mapper.HasTerminated)
    {
        failures++;
        Console.Error.WriteLine("Validation warning: the log does not end with a Terminate lifecycle record.");
    }

    Console.Error.WriteLine(
        $"Inspected {records} record(s), emitted {frames} normalized frame(s), " +
        $"encountered {failures} validation issue(s).");
    return failures == 0 ? 0 : 4;
}

static int RunSelfTest()
{
    try
    {
        TestFramingAtEverySplit();
        TestParserAndMapper();
        TestAccelerationFallbackAndAtomicRejection();
        TestFlyerAndBoostMappings();
        TestStopPreservesConfiguration();
        TestReplaySourceCompletionAsync().GetAwaiter().GetResult();
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
        AssertNear(
            (flight.Timestamp - frames[0].Timestamp).TotalMilliseconds,
            1009.3,
            0.01,
            "replay timestamp delta");

        var impact = frames.Single(frame => frame.RawKind == "PostEvent:4000");
        AssertNear(impact.Impact, 0.4, 1e-9, "impact");
        AssertNear(impact.EngineRumble, flight.EngineRumble, 1e-9, "persistent engine state");

        var terminated = frames.Single(frame => frame.RawKind == "Terminate");
        Assert(!terminated.HasAnySignal, "Terminate did not emit a neutral frame.");
    }
    finally
    {
        CultureInfo.CurrentCulture = previousCulture;
    }
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

    var cruise = Apply(flyer, "<Log TimeStamp=\"2\"><PostEvent Key=\"30\" DataSize=\"20\"><XyzFloat32 Type=\"137\" X=\"0\" Y=\"0.5\" Z=\"-0.3\" /><Float32 Type=\"25\" Value=\"0.75\" /><Float32 Type=\"25\" Value=\"0\" /></PostEvent></Log>");
    AssertNear(cruise!.GForceVertical, 0.5, 1e-9, "Flyer cruise vertical G");
    AssertNear(cruise.EngineRumble, 0.75, 1e-9, "Flyer cruise N1");
    AssertNear(cruise.LandingGear, 0, 1e-9, "Flyer cruise gear");

    var racer = new DBoxSdkSampleTelemetryMapper();
    Apply(racer, "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" /></Log>");
    Apply(racer, "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"40\" Meaning=\"5\" FieldCount=\"1\"><Field Type=\"25\" Flags=\"0\" Meaning=\"24\" Offset=\"0\" TypeName=\"Float32\" /></RegisterEvent></Log>");
    Apply(racer, "<Log TimeStamp=\"0.2\"><RegisterEvent Key=\"41\" Meaning=\"6\" FieldCount=\"0\" /></Log>");
    var boostStart = Apply(racer, "<Log TimeStamp=\"1\"><PostEvent Key=\"40\" DataSize=\"4\"><Float32 Type=\"25\" Value=\"0.75\" /></PostEvent></Log>");
    AssertNear(boostStart!.Boost, 0.75, 1e-9, "Racer boost start");
    var boostStop = Apply(racer, "<Log TimeStamp=\"2\"><PostEvent Key=\"41\" DataSize=\"0\" /></Log>");
    AssertNear(boostStop!.Boost, 0, 1e-9, "Racer boost stop");
    AssertNear(boostStop.Afterburner, 0, 1e-9, "Racer boost is not afterburner");
}

static async Task TestReplaySourceCompletionAsync()
{
    var path = Path.Combine(
        Path.GetTempPath(),
        $"moza-dbox-sanitized-{Guid.NewGuid():N}.log");
    const string fixture =
        "<Log TimeStamp=\"0\"><Initialize AppKey=\"SampleRacer\" /></Log>\n" +
        "<Log TimeStamp=\"0.1\"><RegisterEvent Key=\"50\" Meaning=\"2\" FieldCount=\"1\"><Field Type=\"23\" Flags=\"0\" Meaning=\"1\" Offset=\"0\" TypeName=\"Int32\" /></RegisterEvent></Log>\n" +
        "<Log TimeStamp=\"0.2\"><PostEvent Key=\"50\" DataSize=\"4\"><Int32 Type=\"23\" Value=\"3000\" /></PostEvent></Log>\n" +
        "<Log TimeStamp=\"0.3\"><Terminate /></Log>\n";

    try
    {
        await File.WriteAllTextAsync(
            path,
            fixture,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using var source = new DBoxSdkSampleLogTelemetrySource(path, replaySpeed: 0);
        var frames = new List<StarCitizenTelemetryFrame>();
        await foreach (var frame in source.ReadFramesAsync(CancellationToken.None))
        {
            frames.Add(frame);
            if (frame.RawKind == "ReplayComplete")
            {
                break;
            }
        }

        Assert(frames.Any(frame => frame.RawKind == "PostEvent:50"), "Replay source emitted no mapped post.");
        Assert(frames[^1].RawKind == "ReplayComplete", "Replay source emitted no completion marker.");
        Assert(!frames[^1].HasAnySignal, "Replay completion frame was not neutral.");
        Assert(source.Status.StartsWith("Replay complete:", StringComparison.Ordinal), "Replay source did not report completion.");
    }
    finally
    {
        File.Delete(path);
    }
}

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
            normalizedFrame.Source == "D-BOX SDK sample-format XML observer",
            "Observer did not label its normalized frame source.");
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
        "outside the SDK sample observer allowlist");
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
