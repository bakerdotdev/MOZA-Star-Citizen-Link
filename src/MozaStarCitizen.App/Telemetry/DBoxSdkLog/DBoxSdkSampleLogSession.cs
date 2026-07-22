using System.IO;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed class DBoxSdkSampleLogSession
{
    public const long MaximumLogBytes = 128L * 1024 * 1024;
    public const long MaximumRecordCount = 250_000;
    public const int MaximumSchemaCount = 4_096;
    public const int MaximumFieldsPerRecord = 256;

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

    private readonly DBoxSdkSampleTelemetryMapper _mapper = new();
    private readonly SampleLifecycleValidator _lifecycle = new();
    private double? _previousElapsedMilliseconds;
    private int _initializeCount;

    public DBoxSdkSampleTelemetryMapper Mapper => _mapper;

    public long RecordCount { get; private set; }

    public long FrameCount { get; private set; }

    public DBoxSdkSampleProcessedRecord Process(DBoxSdkLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RecordCount >= MaximumRecordCount)
        {
            throw new InvalidDataException(
                $"The SDK sample log exceeded the {MaximumRecordCount}-record session limit.");
        }

        if (_lifecycle.HasTerminated || _mapper.HasTerminated)
        {
            throw new InvalidDataException(
                "The SDK sample log contains a record after Terminate.");
        }

        if (!AllowedMethods.TryGetValue(record.Method, out var expectedMethodId))
        {
            throw new InvalidDataException(
                $"Method '{record.Method}' is outside the SDK sample allowlist.");
        }

        if (record.MethodId != expectedMethodId)
        {
            throw new InvalidDataException(
                $"Method '{record.Method}' must carry MethodId {expectedMethodId}.");
        }

        if (!double.IsFinite(record.ElapsedMilliseconds) ||
            record.ElapsedMilliseconds < 0)
        {
            throw new InvalidDataException(
                "The SDK sample log contains an invalid negative or non-finite timestamp.");
        }

        if (_previousElapsedMilliseconds is { } previous &&
            record.ElapsedMilliseconds < previous)
        {
            throw new InvalidDataException(
                "The SDK sample log contains a timestamp regression.");
        }

        if (record.Fields.Count > MaximumFieldsPerRecord ||
            record.Values.Count > MaximumFieldsPerRecord)
        {
            throw new InvalidDataException(
                $"An SDK sample record exceeded the {MaximumFieldsPerRecord}-field limit.");
        }

        if (RecordCount == 0 && record.Method != "Initialize")
        {
            throw new InvalidDataException(
                "Initialize must be the first complete record in an SDK sample log.");
        }

        if (record.Method == "Initialize")
        {
            _initializeCount++;
            if (_initializeCount != 1)
            {
                throw new InvalidDataException(
                    "An SDK sample log must contain exactly one Initialize record.");
            }
        }

        _lifecycle.Apply(record.Method);
        var mapped = _mapper.TryApply(record, out var frame, out var warning);
        if (record.Method == "Initialize" && !_mapper.IsSupportedSample)
        {
            throw new InvalidDataException(
                $"Refusing AppKey '{_mapper.AppKey ?? "(missing)"}'. " +
                "Only SampleRacer and SampleFlyer logs are accepted.");
        }

        if (warning is not null)
        {
            throw new InvalidDataException(
                $"The SDK sample record failed schema or payload validation: {warning}");
        }

        if (_mapper.RegisteredSchemaCount > MaximumSchemaCount)
        {
            throw new InvalidDataException(
                $"The SDK sample log exceeded the {MaximumSchemaCount}-schema session limit.");
        }

        _previousElapsedMilliseconds = record.ElapsedMilliseconds;
        RecordCount++;
        if (mapped && frame is not null)
        {
            FrameCount++;
        }

        return new DBoxSdkSampleProcessedRecord(expectedMethodId, frame);
    }

    public void ValidateComplete()
    {
        if (_initializeCount != 1 || !_mapper.IsSupportedSample)
        {
            throw new InvalidDataException(
                "Exactly one supported Initialize record is required.");
        }

        if (!_lifecycle.HasTerminated || !_mapper.HasTerminated)
        {
            throw new InvalidDataException(
                "The SDK sample log does not end with a Terminate lifecycle record.");
        }

        if (_mapper.RegisteredSchemaCount == 0 ||
            _mapper.PostCount == 0 ||
            FrameCount == 0)
        {
            throw new InvalidDataException(
                "The SDK sample log has no complete schema/post/frame sequence.");
        }
    }

    public static void ValidateFileLength(long length)
    {
        if (length < 0 || length > MaximumLogBytes)
        {
            throw new InvalidDataException(
                $"The SDK sample log exceeded the {MaximumLogBytes / (1024 * 1024)} MiB session limit.");
        }
    }

    public static void ValidateXmlRecordText(string xml)
    {
        if (xml.Contains("<!--", StringComparison.Ordinal) ||
            xml.Contains("<?", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Comments and processing instructions are not accepted in SDK sample records.");
        }
    }

    public static void ValidateFramingProgress(DBoxSdkXmlRecordFramer framer)
    {
        if (framer.DiscardedNonWhitespaceCharacters > 0)
        {
            throw new InvalidDataException(
                "The SDK sample log contains non-whitespace text outside complete Log records.");
        }
    }

    public static void ValidateFramingComplete(DBoxSdkXmlRecordFramer framer)
    {
        ValidateFramingProgress(framer);
        if (framer.HasNonWhitespaceBufferedContent)
        {
            throw new InvalidDataException(
                "The SDK sample log ends with an incomplete XML record.");
        }
    }

    private sealed class SampleLifecycleValidator
    {
        private bool _initialized;
        private bool _opened;
        private bool _closed;
        private bool _running;

        public bool HasTerminated { get; private set; }

        public void Apply(string method)
        {
            switch (method)
            {
                case "Initialize":
                    Require(!_initialized, "Initialize may occur only once.");
                    _initialized = true;
                    break;
                case "RegisterEvent":
                    Require(
                        _initialized && !_closed,
                        "RegisterEvent is outside an initialized run.");
                    break;
                case "Open":
                    Require(
                        _initialized && !_opened && !_closed,
                        "Open is out of lifecycle order.");
                    _opened = true;
                    break;
                case "ResetState":
                case "PostEvent":
                    Require(
                        _opened && !_closed,
                        $"{method} requires an open run.");
                    break;
                case "Start":
                    Require(
                        _opened && !_closed && !_running,
                        "Start is out of lifecycle order.");
                    _running = true;
                    break;
                case "Stop":
                    Require(
                        _opened && !_closed && _running,
                        "Stop requires a running sample.");
                    _running = false;
                    break;
                case "Close":
                    Require(
                        _opened && !_closed && !_running,
                        "Close requires an open, stopped sample.");
                    _closed = true;
                    break;
                case "Terminate":
                    Require(
                        _initialized && !_running && (!_opened || _closed),
                        "Terminate requires any open sample to be stopped and closed.");
                    HasTerminated = true;
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

public sealed record DBoxSdkSampleProcessedRecord(
    int ExpectedMethodId,
    StarCitizenTelemetryFrame? Frame);
