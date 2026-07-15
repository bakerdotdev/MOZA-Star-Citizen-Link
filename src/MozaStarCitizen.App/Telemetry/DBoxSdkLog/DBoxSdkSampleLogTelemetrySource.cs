using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed class DBoxSdkSampleLogTelemetrySource : IStarCitizenTelemetrySource
{
    private readonly string _path;
    private readonly double _replaySpeed;
    private DBoxSdkSampleTelemetryMapper _mapper = new();
    private string _status = "Not initialized.";
    private long _recordCount;
    private long _frameCount;
    private long _parseFailureCount;
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

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException("The configured D-BOX SDK sample log was not found.", _path);
        }

        _status = $"Ready to replay {_path} at {FormatSpeed(_replaySpeed)}.";
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        _mapper = new DBoxSdkSampleTelemetryMapper();
        _recordCount = 0;
        _frameCount = 0;
        _parseFailureCount = 0;
        _lastWarning = null;
        _replayCompleted = false;

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);

        var framer = new DBoxSdkXmlRecordFramer();
        var buffer = new char[16 * 1024];
        double? previousElapsedMilliseconds = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_replayCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }

            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (_mapper.AppKey is null)
                {
                    throw new InvalidDataException(
                        "The configured XML log ended without an Initialize record.");
                }

                if (framer.HasNonWhitespaceBufferedContent ||
                    framer.DiscardedNonWhitespaceCharacters > 0)
                {
                    _parseFailureCount++;
                    _lastWarning =
                        "The replay contained discarded text or an incomplete final XML record.";
                }
                else if (!_mapper.HasTerminated)
                {
                    _lastWarning =
                        "The replay ended without Terminate; a neutral frame was forced.";
                }

                _replayCompleted = true;
                _status =
                    $"Replay complete: {_recordCount} record(s), {_frameCount} mapped frame(s), " +
                    $"{_parseFailureCount} parse failure(s).";
                _frameCount++;
                yield return new StarCitizenTelemetryFrame
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = "D-BOX SDK sample XML replay",
                    RawKind = "ReplayComplete"
                };
                continue;
            }

            foreach (var xml in framer.Append(new string(buffer, 0, read)))
            {
                if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var parseError) ||
                    record is null)
                {
                    _parseFailureCount++;
                    _lastWarning = parseError ?? "Unknown XML parse error.";
                    continue;
                }

                _recordCount++;
                if (_replaySpeed > 0 &&
                    previousElapsedMilliseconds is { } previous)
                {
                    var elapsed = record.ElapsedMilliseconds - previous;
                    if (elapsed > 0)
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(Math.Min(elapsed / _replaySpeed, 10_000)),
                            cancellationToken);
                    }
                }

                previousElapsedMilliseconds = record.ElapsedMilliseconds;
                var mapped = _mapper.TryApply(record, out var frame, out var warning);
                if (warning is not null)
                {
                    _lastWarning = warning;
                }

                if (record.Method == "Initialize" && !_mapper.IsSupportedSample)
                {
                    throw new InvalidDataException(
                        $"The configured XML log has AppKey '{_mapper.AppKey ?? "(missing)"}'. " +
                        "This source accepts only logs self-identifying as SampleRacer or SampleFlyer.");
                }

                if (mapped && frame is not null)
                {
                    _frameCount++;
                    _status =
                        $"Reading {_mapper.AppKey}: {_recordCount} record(s), {_frameCount} frame(s), " +
                        $"{_parseFailureCount} parse failure(s).";
                    yield return frame;
                }
            }
        }
    }

    public Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> lines =
        [
            $"D-BOX SDK sample log: {_path}",
            $"Mode: Replay; replay speed: {FormatSpeed(_replaySpeed)}",
            $"Allowed self-identified AppKeys: SampleRacer, SampleFlyer",
            $"Observed AppKey/build/API: {_mapper.AppKey ?? "(none)"}/{_mapper.AppBuild?.ToString() ?? "(none)"}/{_mapper.ApiKey ?? "(none)"}",
            $"Records/frames/parse failures: {_recordCount}/{_frameCount}/{_parseFailureCount}",
            $"Schemas/posts/validation failures: {_mapper.RegisteredSchemaCount}/{_mapper.PostCount}/{_mapper.ValidationFailureCount}",
            $"Mapped/unmapped field observations: {_mapper.MappedFieldCount}/{_mapper.UnmappedFieldCount}",
            $"Replay completed: {_replayCompleted}",
            $"Framing/parser warning: {_lastWarning ?? "(none)"}",
            "Safety boundary: reads only this explicit file; no game, EAC, process, service, registry, network, or D-BOX handler access."
        ];
        return Task.FromResult(lines);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FormatSpeed(double speed) =>
        speed == 0 ? "unthrottled" : $"{speed:0.###}x";

}
