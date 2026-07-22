using System.Runtime.CompilerServices;
using System.Text;
using MozaStarCitizen.App.Telemetry;
using MozaStarCitizen.App.Telemetry.DBoxSdkLog;

internal sealed class DBoxSampleLogObserver
{
    private static readonly TimeSpan CompletionSettleTime = TimeSpan.FromMilliseconds(250);

    private readonly string _path;
    private readonly TimeSpan _idleTimeout;
    private string _status = "Not started.";

    public DBoxSampleLogObserver(string path, TimeSpan idleTimeout)
    {
        _path = DBoxSdkLocalFilePolicy.GetValidatedExistingFilePath(
            path,
            requireFullyQualified: true);
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
        var validatedPath = DBoxSdkLocalFilePolicy.GetValidatedExistingFilePath(
            _path,
            requireFullyQualified: true);
        _status = $"Observing explicit local file: {_path}";
        await using var stream = new FileStream(
            validatedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _ = DBoxSdkLocalFilePolicy.GetValidatedExistingFilePath(
            validatedPath,
            requireFullyQualified: true);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);

        var framer = new DBoxSdkXmlRecordFramer();
        var session = new DBoxSdkSampleLogSession();
        var buffer = new char[16 * 1024];
        var lastDataUtc = DateTimeOffset.UtcNow;
        var greatestObservedLength = stream.Length;
        DBoxSdkSampleLogSession.ValidateFileLength(greatestObservedLength);

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
            DBoxSdkSampleLogSession.ValidateFileLength(greatestObservedLength);

            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read > 0)
            {
                var lengthAfterRead = stream.Length;
                if (lengthAfterRead < greatestObservedLength)
                {
                    throw new InvalidDataException(
                        "The observed file was truncated. The observer will not reopen or follow replacements.");
                }

                greatestObservedLength = lengthAfterRead;
                DBoxSdkSampleLogSession.ValidateFileLength(greatestObservedLength);
                lastDataUtc = DateTimeOffset.UtcNow;
                var framedRecords = framer.Append(new string(buffer, 0, read));
                DBoxSdkSampleLogSession.ValidateFramingProgress(framer);

                foreach (var xml in framedRecords)
                {
                    DBoxSdkSampleLogSession.ValidateXmlRecordText(xml);

                    if (!DBoxSdkXmlLogParser.TryParse(xml, out var record, out var parseError) ||
                        record is null)
                    {
                        throw new InvalidDataException(
                            $"The observed log contains an invalid XML record: {parseError ?? "unknown parser error"}");
                    }

                    var processed = session.Process(record);
                    var mapper = session.Mapper;
                    var observedUtc = DateTimeOffset.UtcNow;
                    var normalizedFrame = processed.Frame is { } frame
                        ? frame with
                        {
                            Timestamp = observedUtc,
                            Source = $"D-BOX SDK {mapper.AppKey} XML observer"
                        }
                        : null;
                    _status =
                        $"Observing {mapper.AppKey}: {session.RecordCount} complete record(s), " +
                        $"{mapper.PostCount} post(s).";
                    yield return new DBoxSampleObservation
                    {
                        Sequence = session.RecordCount,
                        ObservedUtc = observedUtc,
                        ElapsedMilliseconds = record.ElapsedMilliseconds,
                        Method = record.Method,
                        MethodId = processed.ExpectedMethodId,
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
            if (session.Mapper.HasTerminated && idle >= CompletionSettleTime)
            {
                DBoxSdkSampleLogSession.ValidateFramingComplete(framer);
                session.ValidateComplete();

                _status =
                    $"Observation complete: {session.RecordCount} record(s), " +
                    $"{session.Mapper.PostCount} post(s), " +
                    $"{session.Mapper.RegisteredSchemaCount} schema(s).";
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
