using System.Net.Http;
using System.Runtime.CompilerServices;
using System.IO;
using MozaStarCitizen.App.Diagnostics;

namespace MozaStarCitizen.App.Telemetry;

public sealed class HttpJsonTelemetrySource : IStarCitizenTelemetrySource
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly TimeSpan _pollInterval;
    private string _status;

    public HttpJsonTelemetrySource(string name, string endpoint)
    {
        Name = name;
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        _pollInterval = GetPollInterval();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        _status = $"Polling {_endpoint} every {(int)_pollInterval.TotalMilliseconds} ms.";
    }

    public string Name { get; }

    public string Status => _status;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        _status = $"Connected to {_endpoint}.";
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            StarCitizenTelemetryFrame? frame = null;
            try
            {
                var json = await _httpClient.GetStringAsync(_endpoint, cancellationToken);
                if (TelemetryJsonFrameMapper.TryMap(json, Name, out var mappedFrame, out var summary))
                {
                    _status = $"Receiving JSON telemetry from {_endpoint}: {summary}.";
                    frame = mappedFrame;
                }
                else
                {
                    _status = $"Connected to {_endpoint}, but the JSON payload did not contain known telemetry fields.";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _status = $"Waiting for telemetry at {_endpoint}: {ex.Message}";
                AppLog.Write($"HTTP telemetry poll failed for {_endpoint}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            if (frame is not null)
            {
                yield return frame;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    public Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([
            $"HTTP telemetry endpoint: {_endpoint}",
            $"Poll interval: {(int)_pollInterval.TotalMilliseconds} ms",
            $"Status: {_status}"
        ]);

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TimeSpan GetPollInterval()
    {
        var value = Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY_POLL_MS");
        return int.TryParse(value, out var milliseconds)
            ? TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 16, 1000))
            : TimeSpan.FromMilliseconds(50);
    }
}
