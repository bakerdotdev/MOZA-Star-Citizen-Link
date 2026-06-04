using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MozaStarCitizen.App.Telemetry;

public static class TelemetryJsonFrameMapper
{
    public static bool TryMap(
        string json,
        string source,
        out StarCitizenTelemetryFrame frame,
        out string summary)
    {
        frame = new StarCitizenTelemetryFrame { Source = source };
        summary = "no telemetry values";

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = GetTelemetryRoot(document.RootElement);
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var booleans = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(root, null, values, booleans, strings);

            frame = new StarCitizenTelemetryFrame
            {
                Timestamp = TryGetTimestamp(strings) ?? DateTimeOffset.Now,
                Source = source,
                EngineRumble = PickSignal(values, ["engineRumble", "engineVibration", "engine", "thrust", "throttle", "rpm"]),
                EngineFrequencyHz = PickFrequency(values),
                Atmosphere = PickSignal(values, ["atmosphere", "atmo", "airDensity", "density", "wind", "turbulence"]),
                GForceLongitudinal = PickSigned(values, ["gForceLongitudinal", "longitudinalG", "surge", "accelerationZ", "gZ"]),
                GForceLateral = PickSigned(values, ["gForceLateral", "lateralG", "sway", "accelerationX", "gX"]),
                GForceVertical = PickSigned(values, ["gForceVertical", "verticalG", "heave", "accelerationY", "gY"]),
                Boost = PickSignal(values, ["boost"]),
                Afterburner = PickSignal(values, ["afterburner", "afterBurner"]),
                Impact = PickSignal(values, ["impact", "hit", "collision", "explosion", "damage"]),
                WeaponFire = PickSignal(values, ["weaponFire", "gunfire", "recoil", "fire"]),
                Countermeasure = PickSignal(values, ["countermeasure", "flare", "chaff"]),
                LandingGear = PickSignal(values, ["landingGear", "gear"]),
                Decoupled = PickBoolean(booleans, ["decoupled", "decouple"]),
                RawKind = PickString(strings, ["kind", "event", "type", "signal"])
            };

            summary = BuildSummary(frame);
            return frame.HasAnySignal;
        }
        catch (JsonException ex)
        {
            summary = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static JsonElement GetTelemetryRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.GetArrayLength() == 0 ? root : root[root.GetArrayLength() - 1];
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        foreach (var propertyName in new[] { "telemetry", "frame", "data", "payload", "state", "signals" })
        {
            if (root.TryGetProperty(propertyName, out var child))
            {
                return child;
            }
        }

        return root;
    }

    private static void Flatten(
        JsonElement element,
        string? prefix,
        IDictionary<string, double> values,
        IDictionary<string, bool> booleans,
        IDictionary<string, string> strings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = string.IsNullOrWhiteSpace(prefix)
                        ? property.Name
                        : $"{prefix}.{property.Name}";
                    Flatten(property.Value, name, values, booleans, strings);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    Flatten(child, $"{prefix}.{index}", values, booleans, strings);
                    index++;
                }

                break;

            case JsonValueKind.Number:
                if (!string.IsNullOrWhiteSpace(prefix) && element.TryGetDouble(out var number))
                {
                    values[Normalize(prefix)] = number;
                }

                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    booleans[Normalize(prefix)] = element.GetBoolean();
                }

                break;

            case JsonValueKind.String:
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    strings[Normalize(prefix)] = element.GetString() ?? string.Empty;
                }

                break;
        }
    }

    private static DateTimeOffset? TryGetTimestamp(IReadOnlyDictionary<string, string> strings)
    {
        foreach (var name in new[] { "timestamp", "time", "utc", "datetime" })
        {
            if (TryFind(strings, name, out var value) &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    private static double PickFrequency(IReadOnlyDictionary<string, double> values)
    {
        foreach (var name in new[] { "engineFrequencyHz", "frequencyHz", "rumbleFrequencyHz", "hz" })
        {
            if (TryFind(values, name, out var value))
            {
                return Math.Clamp(Math.Abs(value), 0, 120);
            }
        }

        var rpm = PickRaw(values, ["rpm", "engineRpm"]);
        return rpm > 0 ? Math.Clamp(rpm / 90, 8, 80) : 0;
    }

    private static double PickSignal(IReadOnlyDictionary<string, double> values, string[] names)
    {
        var raw = PickRaw(values, names);
        if (raw <= 0)
        {
            return 0;
        }

        return NormalizeSignal(raw);
    }

    private static double PickRaw(IReadOnlyDictionary<string, double> values, string[] names)
    {
        foreach (var name in names)
        {
            if (TryFind(values, name, out var value))
            {
                return Math.Abs(value);
            }
        }

        foreach (var name in names)
        {
            var normalized = Normalize(name);
            var match = values
                .Where(pair => pair.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key.Length)
                .Select(pair => Math.Abs(pair.Value))
                .FirstOrDefault();
            if (match > 0)
            {
                return match;
            }
        }

        return 0;
    }

    private static double PickSigned(IReadOnlyDictionary<string, double> values, string[] names)
    {
        foreach (var name in names)
        {
            if (TryFind(values, name, out var value))
            {
                return value;
            }
        }

        foreach (var name in names)
        {
            var normalized = Normalize(name);
            var match = values
                .Where(pair => pair.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key.Length)
                .Select(pair => (double?)pair.Value)
                .FirstOrDefault();
            if (match is not null)
            {
                return match.Value;
            }
        }

        return 0;
    }

    private static bool? PickBoolean(IReadOnlyDictionary<string, bool> values, string[] names)
    {
        foreach (var name in names)
        {
            if (TryFind(values, name, out var value))
            {
                return value;
            }
        }

        foreach (var name in names)
        {
            var normalized = Normalize(name);
            var match = values
                .Where(pair => pair.Key.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key.Length)
                .Select(pair => (bool?)pair.Value)
                .FirstOrDefault();
            if (match is not null)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string? PickString(IReadOnlyDictionary<string, string> values, string[] names)
    {
        foreach (var name in names)
        {
            if (TryFind(values, name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryFind<T>(IReadOnlyDictionary<string, T> values, string name, out T value)
    {
        if (values.TryGetValue(Normalize(name), out value!))
        {
            return true;
        }

        var normalized = Normalize(name);
        foreach (var pair in values)
        {
            if (pair.Key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private static double NormalizeSignal(double value)
    {
        value = Math.Abs(double.IsFinite(value) ? value : 0);
        if (value <= 1)
        {
            return value;
        }

        if (value <= 100)
        {
            return value / 100;
        }

        return Math.Clamp(value / 1000, 0, 1);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string BuildSummary(StarCitizenTelemetryFrame frame)
    {
        var values = new List<string>();
        Add(values, "engine", frame.EngineRumble);
        Add(values, "atmo", frame.Atmosphere);
        Add(values, "boost", frame.Boost);
        Add(values, "afterburner", frame.Afterburner);
        Add(values, "impact", frame.Impact);
        Add(values, "weapon", frame.WeaponFire);
        Add(values, "gear", frame.LandingGear);

        var gMagnitude = Math.Sqrt(
            frame.GForceLongitudinal * frame.GForceLongitudinal +
            frame.GForceLateral * frame.GForceLateral +
            frame.GForceVertical * frame.GForceVertical);
        Add(values, "g", gMagnitude);

        if (frame.Decoupled is not null)
        {
            values.Add($"decoupled={frame.Decoupled.Value}");
        }

        return values.Count == 0 ? "no force-relevant values" : string.Join(", ", values);
    }

    private static void Add(ICollection<string> values, string name, double value)
    {
        if (Math.Abs(value) > 0.001)
        {
            values.Add($"{name}={value:0.###}");
        }
    }
}
