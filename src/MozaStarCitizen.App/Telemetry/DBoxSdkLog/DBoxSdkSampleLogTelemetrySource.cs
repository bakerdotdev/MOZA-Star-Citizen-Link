using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed class DBoxSdkSampleLogTelemetrySource : IStarCitizenTelemetrySource
{
    private readonly string _path;
    private readonly double _replaySpeed;
    private DBoxSdkSampleLogSession _session = new();
    private string _status = "Not initialized.";
    private long _validationFailureCount;
    private string? _lastWarning;
    private bool _replayCompleted;

    public DBoxSdkSampleLogTelemetrySource(
        string path,
        double replaySpeed = 1)
    {
        _path = DBoxSdkLocalFilePolicy.GetValidatedPath(path);
        _replaySpeed = double.IsFinite(replaySpeed) && replaySpeed is >= 0 and <= 100
            ? replaySpeed
            : 1;
    }

    public string Name => "D-BOX SDK sample XML log";

    public string Status => _status;

    public TelemetryOutputPolicy OutputPolicy => TelemetryOutputPolicy.VisualizationOnly;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = DBoxSdkLocalFilePolicy.GetValidatedExistingFilePath(_path);

        _status = $"Ready to replay {_path} at {FormatSpeed(_replaySpeed)}.";
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _session = new DBoxSdkSampleLogSession();
        _validationFailureCount = 0;
        _lastWarning = null;
        _replayCompleted = false;

        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch (Exception ex) when (IsReplayInputFailure(ex))
        {
            RecordReplayRejection(ex);
            throw;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (IsReplayInputFailure(ex))
        {
            RecordReplayRejection(ex);
            throw;
        }

        await using var streamLease = stream;
        var replayItems = new List<ReplayItem>();
        _status = $"Validating {_path} before replay.";

        try
        {
            DBoxSdkSampleLogSession.ValidateFileLength(stream.Length);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: true);
            var framer = new DBoxSdkXmlRecordFramer();
            var buffer = new char[16 * 1024];

            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    DBoxSdkSampleLogSession.ValidateFramingComplete(framer);
                    _session.ValidateComplete();
                    break;
                }

                var framedRecords = framer.Append(new string(buffer, 0, read));
                DBoxSdkSampleLogSession.ValidateFramingProgress(framer);
                foreach (var xml in framedRecords)
                {
                    DBoxSdkSampleLogSession.ValidateXmlRecordText(xml);
                    if (!DBoxSdkXmlLogParser.TryParse(
                            xml,
                            out var record,
                            out var parseError) ||
                        record is null)
                    {
                        throw new InvalidDataException(
                            "The SDK sample log contains an invalid XML record: " +
                            (parseError ?? "Unknown XML parse error."));
                    }

                    var processed = _session.Process(record);
                    replayItems.Add(new ReplayItem(
                        record.ElapsedMilliseconds,
                        processed.Frame));
                    _status =
                        $"Validating {_session.Mapper.AppKey}: {_session.RecordCount} record(s), " +
                        $"{_session.FrameCount} frame(s).";
                }
            }
        }
        catch (Exception ex) when (IsReplayInputFailure(ex))
        {
            RecordReplayRejection(ex);
            throw;
        }

        _status =
            $"Validated {_session.Mapper.AppKey}: {_session.RecordCount} record(s), " +
            $"{_session.FrameCount} frame(s); replaying at {FormatSpeed(_replaySpeed)}.";
        double? previousElapsedMilliseconds = null;
        foreach (var item in replayItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_replaySpeed > 0 &&
                previousElapsedMilliseconds is { } previous)
            {
                var elapsed = item.ElapsedMilliseconds - previous;
                if (elapsed > 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Min(elapsed / _replaySpeed, 10_000)),
                        cancellationToken);
                }
            }

            previousElapsedMilliseconds = item.ElapsedMilliseconds;
            if (item.Frame is { } frame)
            {
                yield return frame;
            }
        }

        _replayCompleted = true;
        _status =
            $"Replay complete: {_session.RecordCount} record(s), " +
            $"{_session.FrameCount} mapped frame(s), " +
            $"{_validationFailureCount} validation failure(s).";
        yield return new StarCitizenTelemetryFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = $"D-BOX SDK {_session.Mapper.AppKey} XML replay",
            SourceKind = TelemetrySourceKind.DBoxSdkSample,
            ApplicationKey = _session.Mapper.AppKey,
            Boundary = TelemetryFrameBoundary.ReplayComplete,
            RawKind = "ReplayComplete"
        };
    }

    public Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> lines =
        [
            $"D-BOX SDK sample log: {_path}",
            $"Mode: Replay; replay speed: {FormatSpeed(_replaySpeed)}",
            $"Output policy: {OutputPolicy}; hardware effects disabled",
            $"Allowed self-identified AppKeys: SampleRacer, SampleFlyer",
            $"Observed AppKey/build/API credential: {_session.Mapper.AppKey ?? "(none)"}/{_session.Mapper.AppBuild?.ToString() ?? "(none)"}/{(_session.Mapper.ApiKey is null ? "(none)" : "(present; redacted)")}",
            $"Records/frames/validation failures: {_session.RecordCount}/{_session.FrameCount}/{_validationFailureCount}",
            $"Schemas/posts/validation failures: {_session.Mapper.RegisteredSchemaCount}/{_session.Mapper.PostCount}/{_session.Mapper.ValidationFailureCount}",
            $"Mapped/unmapped field observations: {_session.Mapper.MappedFieldCount}/{_session.Mapper.UnmappedFieldCount}",
            $"Replay completed: {_replayCompleted}",
            $"Validation warning: {_lastWarning ?? "(none)"}",
            "Safety boundary: reads only this explicit file; no game, EAC, process, service, registry, network, or D-BOX handler access."
        ];
        return Task.FromResult(lines);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FormatSpeed(double speed) =>
        speed == 0 ? "unthrottled" : $"{speed:0.###}x";

    private static bool IsReplayInputFailure(Exception exception) =>
        exception is
            InvalidDataException or
            IOException or
            ArgumentException or
            NotSupportedException or
            UnauthorizedAccessException;

    private void RecordReplayRejection(Exception exception)
    {
        _validationFailureCount++;
        _lastWarning = exception.Message;
        _status = $"Replay rejected: {exception.Message}";
    }

    private sealed record ReplayItem(
        double ElapsedMilliseconds,
        StarCitizenTelemetryFrame? Frame);
}
