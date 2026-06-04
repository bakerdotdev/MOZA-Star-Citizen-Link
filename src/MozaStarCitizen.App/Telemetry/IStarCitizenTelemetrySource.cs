using System.Runtime.CompilerServices;

namespace MozaStarCitizen.App.Telemetry;

public interface IStarCitizenTelemetrySource : IAsyncDisposable
{
    string Name { get; }

    string Status { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken);
}

public sealed class NoTelemetrySource : IStarCitizenTelemetrySource
{
    public string Name => "No telemetry source";

    public string Status => "No telemetry source is configured.";

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        yield break;
    }

    public Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([Status]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
