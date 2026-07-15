using System.Globalization;
using System.IO;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed record DBoxSdkFieldDefinition(
    int TypeId,
    int Flags,
    int MeaningId,
    int Offset,
    string? TypeName,
    string? MeaningName);

public sealed record DBoxSdkPostedValue(
    int? TypeId,
    string TypeName,
    IReadOnlyDictionary<string, string> Attributes)
{
    public bool TryGetScalar(out double value) =>
        TryGetFiniteDouble("Value", out value);

    public bool TryGetVector(out DBoxSdkVector value)
    {
        if (TryGetFiniteDouble("X", out var x) &&
            TryGetFiniteDouble("Y", out var y) &&
            TryGetFiniteDouble("Z", out var z))
        {
            value = new DBoxSdkVector(x, y, z);
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetText(out string? value) =>
        Attributes.TryGetValue("Value", out value);

    private bool TryGetFiniteDouble(string name, out double value)
    {
        if (Attributes.TryGetValue(name, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}

public readonly record struct DBoxSdkVector(double X, double Y, double Z);

public static class DBoxSdkLocalFilePolicy
{
    public static string GetValidatedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A D-BOX SDK sample log path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Network and device paths are not accepted; copy the sample log to a local drive.",
                nameof(path));
        }

        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                if (new DriveInfo(root).DriveType == DriveType.Network)
                {
                    throw new ArgumentException(
                        "Mapped network drives are not accepted; copy the sample log to a local drive.",
                        nameof(path));
                }
            }
            catch (IOException)
            {
                // The caller's file existence/open check will provide the path error.
            }
        }

        return fullPath;
    }
}

public sealed record DBoxSdkLogRecord
{
    public required double ElapsedMilliseconds { get; init; }

    public required string Method { get; init; }

    public int? MethodId { get; init; }

    public string? AppKey { get; init; }

    public int? AppBuild { get; init; }

    public string? ApiKey { get; init; }

    public uint? EventKey { get; init; }

    public int? EventMeaningId { get; init; }

    public string? EventMeaningName { get; init; }

    public int? DataSize { get; init; }

    public int? DeclaredFieldCount { get; init; }

    public IReadOnlyList<DBoxSdkFieldDefinition> Fields { get; init; } = [];

    public IReadOnlyList<DBoxSdkPostedValue> Values { get; init; } = [];
}
