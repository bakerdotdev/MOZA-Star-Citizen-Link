using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MozaStarCitizen.App.Diagnostics;

namespace MozaStarCitizen.App.Telemetry;

public sealed class DBoxHaptiSyncTelemetrySource : IStarCitizenTelemetrySource
{
    private static readonly Uri ApiBaseUri = new("http://localhost:42010/");
    private static readonly string[] OpenApiCandidates =
    [
        "swagger/v1/swagger.json",
        "openapi.json",
        "swagger.json",
        "v1/swagger.json"
    ];

    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = ApiBaseUri,
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly List<string> _discoveredPaths = [];
    private HttpJsonTelemetrySource? _telemetryEndpoint;
    private string _status = "Not initialized.";
    private DateTimeOffset _nextDiscoveryAttempt = DateTimeOffset.MinValue;

    public string Name => "D-BOX HaptiSync API";

    public string Status => _status;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("index.html", cancellationToken);
            response.EnsureSuccessStatusCode();
            _status = "Connected to D-BOX HaptiSync API at http://localhost:42010.";
            AppLog.Write("Connected to D-BOX HaptiSync API.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _status = $"Waiting for D-BOX HaptiSync Center API on localhost:42010: {ex.Message}";
            AppLog.Write(_status);
            return;
        }

        await DiscoverOpenApiAsync(cancellationToken);
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_telemetryEndpoint is null)
            {
                await InitializeAsync(cancellationToken);
            }

            if (_telemetryEndpoint is not null)
            {
                await foreach (var frame in _telemetryEndpoint.ReadFramesAsync(cancellationToken))
                {
                    yield return frame with { Source = Name };
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"D-BOX HaptiSync API base: {ApiBaseUri}",
            $"Status: {_status}",
            "Expected minimum HaptiSync Center: 1.3.0"
        };

        if (_discoveredPaths.Count == 0)
        {
            await DiscoverOpenApiAsync(cancellationToken);
        }

        lines.Add($"OpenAPI paths discovered: {_discoveredPaths.Count}");
        foreach (var path in _discoveredPaths.Take(24))
        {
            lines.Add($"  {path}");
        }

        if (_discoveredPaths.Count > 24)
        {
            lines.Add($"  ... {_discoveredPaths.Count - 24} more");
        }

        lines.Add(_telemetryEndpoint is null
            ? "Telemetry endpoint: none discovered. Current public HaptiSync API appears to control D-BOX settings, not expose Star Citizen frames."
            : $"Telemetry endpoint: {_telemetryEndpoint.Status}");

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        if (_telemetryEndpoint is not null)
        {
            await _telemetryEndpoint.DisposeAsync();
        }

        _httpClient.Dispose();
    }

    private async Task DiscoverOpenApiAsync(CancellationToken cancellationToken)
    {
        if (_discoveredPaths.Count > 0)
        {
            return;
        }

        if (DateTimeOffset.Now < _nextDiscoveryAttempt)
        {
            return;
        }

        _nextDiscoveryAttempt = DateTimeOffset.Now.AddSeconds(30);

        foreach (var candidate in OpenApiCandidates)
        {
            try
            {
                var json = await _httpClient.GetStringAsync(candidate, cancellationToken);
                var paths = ReadOpenApiPaths(json);
                if (paths.Count == 0)
                {
                    continue;
                }

                _discoveredPaths.AddRange(paths);
                var telemetryPath = paths.FirstOrDefault(IsLikelyTelemetryPath);
                if (telemetryPath is not null)
                {
                    var endpoint = new Uri(ApiBaseUri, telemetryPath.TrimStart('/')).ToString();
                    _telemetryEndpoint = new HttpJsonTelemetrySource("D-BOX telemetry endpoint", endpoint);
                    _status = $"Discovered possible D-BOX telemetry endpoint: {telemetryPath}";
                }
                else
                {
                    _status = "D-BOX HaptiSync API is reachable; no telemetry endpoint was found in OpenAPI.";
                }

                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                AppLog.Write($"D-BOX OpenAPI discovery failed for {candidate}: {ex.Message}");
            }
        }

        _status = "D-BOX HaptiSync API is reachable, but OpenAPI JSON was not discoverable from common Swagger paths.";
    }

    private static IReadOnlyList<string> ReadOpenApiPaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("paths", out var paths) ||
            paths.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return paths
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLikelyTelemetryPath(string path)
    {
        var text = path.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return text.Contains("telemetry", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("motionstate", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("gamedata", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("gamesignal", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("signals", StringComparison.OrdinalIgnoreCase);
    }
}
