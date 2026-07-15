using System.Runtime.CompilerServices;
using System.Text;
using MozaStarCitizen.App.Telemetry;
using MozaStarCitizen.App.Telemetry.DBoxSdkLog;

internal sealed class DBoxSampleLogObserver
{
    private static readonly TimeSpan CompletionSettleTime = TimeSpan.FromMilliseconds(250);
    private static readonly IReadOnlyDictionary<string, int> AllowedMethods =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Initialize"] = 1,
            ["Terminate"] = 2,
            ["Open"] = 3,
            ["Close"] = 4,
            ["Start"] = 5,
            ["Stop"] = 6,
            ["ResetState"] = 7,
            ["RegisterEvent"] = 8,
            ["PostEvent"] = 9
        };
    private const long MaximumObservedBytes = 128L * 1024 * 1024;
    private const long MaximumRecordCount = 250_000;
    private const int MaximumSchemaCount = 4_096;
    private const int MaximumFieldsPerRecord = 256;

    private readonly string _path;
    private readonly TimeSpan _idleTimeout;
    private string _status = "Not started.";

    public DBoxSampleLogObserver(string path, TimeSpan idleTimeout)
    {
        _path = GetValidatedObserverPath(path);
        if (idleTimeout <= TimeSpan.Zero || idleTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                "The idle timeout must be greater than zero and no more than one hour.");
        }

        _idleTimeout = idleTimeout;
    }

    public string Status => _status;

    public async IAsyncEnumerable<DBoxSampleObservation> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var validatedPath = GetValidatedObserverPath(_path);
        _status = $"Observing explicit local file: {_path}";
        await using var stream = new FileStream(
            validatedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _ = GetValidatedObserverPath(validatedPath);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);

        var framer = new DBoxSdkXmlRecordFramer();
        var mapper = new DBoxSdkSampleTelemetryMapper();
        var buffer = new char[16 * 1024];
        var sequence = 0L;
        var initializeCount = 0;
        var lifecycle = new SampleLifecycleValidator();
        var lastDataUtc = DateTimeOffset.UtcNow;
        var previousElapsedMilliseconds = -1d;
        var greatestObservedLength = stream.Length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentLength = stream.Length;
            if (currentLength < greatestObservedLength)
            {
                throw new InvalidDataException(
                    "The observed file was truncated. The observer will not reopen or follow replacements.");
            }

            greatestObservedLength = currentLength;
            if (greatestObservedLength > MaximumObservedBytes)
            {
                throw new InvalidDataException(
                    $"The observed file exceeded the {MaximumObservedBytes / (1024 * 1024)} MiB session limit.");
            }

            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read > 0)
            {
                lastDataUtc = DateTimeOffset.UtcNow;
                var framedRecords = framer.Append(new string(buffer, 0, read));
                if (framer.DiscardedNonWhitespaceCharacters > 0)
                {
                    throw new InvalidDataException(
                        "The observed log contains non-whitespace text outside complete Log records.");
                }

                foreach (var xml in framedRecords)
                {
                    if (xml.Contains("<!--", StringComparison.Ordinal) ||
                        xml.Contains("<?", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Comments and processing instructions are not accepted in observed records.");
                    }

                    if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var parseError) ||
                        record is null)
                    {
                        throw new InvalidDataException(
                            $"The observed log contains an invalid XML record: {parseError ?? "unknown parser error"}");
                    }

                    if (!AllowedMethods.TryGetValue(record.Method, out var expectedMethodId))
                    {
                        throw new InvalidDataException(
                            $"Method '{record.Method}' is outside the SDK sample observer allowlist.");
                    }

                    if (record.MethodId != expectedMethodId)
                    {
                        throw new InvalidDataException(
                            $"Method '{record.Method}' must carry MethodId {expectedMethodId}.");
                    }

                    if (record.ElapsedMilliseconds < previousElapsedMilliseconds)
                    {
                        throw new InvalidDataException(
                            "The observed log contains a timestamp regression.");
                    }

                    previousElapsedMilliseconds = record.ElapsedMilliseconds;
                    if (record.Fields.Count > MaximumFieldsPerRecord ||
                        record.Values.Count > MaximumFieldsPerRecord)
                    {
                        throw new InvalidDataException(
                            $"An observed record exceeded the {MaximumFieldsPerRecord}-field limit.");
                    }

                    if (sequence >= MaximumRecordCount)
                    {
                        throw new InvalidDataException(
                            $"The observer exceeded the {MaximumRecordCount}-record session limit.");
                    }

                    if (mapper.HasTerminated)
                    {
                        throw new InvalidDataException(
                            "The observed log contains a record after Terminate.");
                    }

                    if (sequence == 0 && record.Method != "Initialize")
                    {
                        throw new InvalidDataException(
                            "Initialize must be the first complete record in an observed sample log.");
                    }

                    if (record.Method == "Initialize")
                    {
                        initializeCount++;
                        if (initializeCount != 1)
                        {
                            throw new InvalidDataException(
                                "An observed file must contain exactly one Initialize record.");
                        }
                    }

                    lifecycle.Apply(record.Method);

                    var mapped = mapper.TryApply(record, out var frame, out var warning);
                    if (record.Method == "Initialize" && !mapper.IsSupportedSample)
                    {
                        throw new InvalidDataException(
                            $"Refusing AppKey '{mapper.AppKey ?? "(missing)"}'. " +
                            "Only SampleRacer and SampleFlyer logs are accepted.");
                    }

                    if (warning is not null)
                    {
                        throw new InvalidDataException(
                            $"The observed record failed schema or payload validation: {warning}");
                    }

                    if (mapper.RegisteredSchemaCount > MaximumSchemaCount)
                    {
                        throw new InvalidDataException(
                            $"The observer exceeded the {MaximumSchemaCount}-schema session limit.");
                    }

                    sequence++;
                    var observedUtc = DateTimeOffset.UtcNow;
                    var normalizedFrame = mapped && frame is not null
                        ? frame with
                        {
                            Timestamp = observedUtc,
                            Source = "D-BOX SDK sample-format XML observer"
                        }
                        : null;
                    _status =
                        $"Observing {mapper.AppKey}: {sequence} complete record(s), " +
                        $"{mapper.PostCount} post(s).";
                    yield return new DBoxSampleObservation
                    {
                        Sequence = sequence,
                        ObservedUtc = observedUtc,
                        ElapsedMilliseconds = record.ElapsedMilliseconds,
                        Method = record.Method,
                        MethodId = expectedMethodId,
                        AppKey = mapper.AppKey,
                        AppBuild = mapper.AppBuild,
                        EventKey = record.EventKey,
                        EventMeaningId = record.EventMeaningId,
                        EventMeaningName = record.EventMeaningName,
                        DataSize = record.DataSize,
                        DeclaredFieldCount = record.DeclaredFieldCount,
                        Fields = record.Fields,
                        Values = record.Values,
                        NormalizedFrame = normalizedFrame
                    };
                }

                continue;
            }

            var idle = DateTimeOffset.UtcNow - lastDataUtc;
            if (mapper.HasTerminated && idle >= CompletionSettleTime)
            {
                if (framer.HasNonWhitespaceBufferedContent)
                {
                    throw new InvalidDataException(
                        "The observed log terminated with an incomplete XML record.");
                }

                _status =
                    $"Observation complete: {sequence} record(s), {mapper.PostCount} post(s), " +
                    $"{mapper.RegisteredSchemaCount} schema(s).";
                yield break;
            }

            if (idle >= _idleTimeout)
            {
                var detail = framer.HasNonWhitespaceBufferedContent
                    ? "an incomplete XML record remains buffered"
                    : "no Terminate record was observed";
                throw new InvalidDataException(
                    $"The explicit log was idle for {_idleTimeout.TotalSeconds:0.###} second(s) and {detail}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static string GetValidatedObserverPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "The observer requires an absolute local file path.",
                nameof(path));
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The observer requires an absolute local file path.",
                nameof(path));
        }

        var fullPath = DBoxSdkLocalFilePolicy.GetValidatedPath(path);
        if (fullPath.IndexOf(':', 2) >= 0)
        {
            throw new ArgumentException(
                "Alternate data streams are not accepted.",
                nameof(path));
        }

        var root = Path.GetPathRoot(fullPath) ??
            throw new ArgumentException("The observer path has no local drive root.", nameof(path));
        var current = root;
        var finalAttributes = GetAttributesWithoutExistenceProbe(current, fullPath);
        if ((finalAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Reparse-point paths are not accepted.", nameof(path));
        }

        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            finalAttributes = GetAttributesWithoutExistenceProbe(current, fullPath);
            if ((finalAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Symlinks, junctions, mount points, and other reparse paths are not accepted.",
                    nameof(path));
            }
        }

        if ((finalAttributes & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException(
                "The observer path must identify an existing ordinary file.",
                nameof(path));
        }

        return fullPath;
    }

    private static FileAttributes GetAttributesWithoutExistenceProbe(
        string componentPath,
        string requestedFile)
    {
        try
        {
            return File.GetAttributes(componentPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                "The explicit D-BOX SDK sample log was not found. The observer does not search for logs.",
                requestedFile,
                ex);
        }
    }

    private sealed class SampleLifecycleValidator
    {
        private bool _initialized;
        private bool _opened;
        private bool _closed;
        private bool _running;

        public void Apply(string method)
        {
            switch (method)
            {
                case "Initialize":
                    Require(!_initialized, "Initialize may occur only once.");
                    _initialized = true;
                    break;
                case "RegisterEvent":
                    Require(_initialized && !_closed, "RegisterEvent is outside an initialized run.");
                    break;
                case "Open":
                    Require(_initialized && !_opened && !_closed, "Open is out of lifecycle order.");
                    _opened = true;
                    break;
                case "ResetState":
                case "PostEvent":
                    Require(_opened && !_closed, $"{method} requires an open run.");
                    break;
                case "Start":
                    Require(_opened && !_closed && !_running, "Start is out of lifecycle order.");
                    _running = true;
                    break;
                case "Stop":
                    Require(_opened && !_closed && _running, "Stop requires a running sample.");
                    _running = false;
                    break;
                case "Close":
                    Require(_opened && !_closed && !_running, "Close requires an open, stopped sample.");
                    _closed = true;
                    break;
                case "Terminate":
                    Require(
                        _initialized && !_running && (!_opened || _closed),
                        "Terminate requires any open sample to be stopped and closed.");
                    break;
                default:
                    throw new InvalidDataException(
                        $"Method '{method}' is outside the lifecycle validator allowlist.");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }
    }
}

internal sealed record DBoxSampleObservation
{
    public required long Sequence { get; init; }

    public required DateTimeOffset ObservedUtc { get; init; }

    public required double ElapsedMilliseconds { get; init; }

    public required string Method { get; init; }

    public required int MethodId { get; init; }

    public string? AppKey { get; init; }

    public int? AppBuild { get; init; }

    public uint? EventKey { get; init; }

    public int? EventMeaningId { get; init; }

    public string? EventMeaningName { get; init; }

    public int? DataSize { get; init; }

    public int? DeclaredFieldCount { get; init; }

    public IReadOnlyList<DBoxSdkFieldDefinition> Fields { get; init; } = [];

    public IReadOnlyList<DBoxSdkPostedValue> Values { get; init; } = [];

    public StarCitizenTelemetryFrame? NormalizedFrame { get; init; }
}
