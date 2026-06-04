using MozaStarCitizen.App.Models;

namespace MozaStarCitizen.App.ForceFeedback;

public sealed class NullForceFeedbackDevice : IForceFeedbackDevice
{
    private readonly string _status;

    public NullForceFeedbackDevice(string? status = null)
    {
        _status = status ?? "Running without hardware output. Telemetry effects are preview-only.";
    }

    public string Name => "Preview output";

    public string Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PrepareAsync(IEnumerable<ForceEffect> effects, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PlayAsync(ForceEffect effect, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(string stateKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
