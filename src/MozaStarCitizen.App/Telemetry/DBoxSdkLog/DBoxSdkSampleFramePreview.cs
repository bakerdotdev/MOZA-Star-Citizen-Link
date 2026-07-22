using System.Globalization;

namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public static class DBoxSdkSampleFramePreview
{
    public static bool ShouldDisplay(
        StarCitizenTelemetryFrame frame,
        long sampleFrameSequence)
    {
        if (frame.SourceKind != TelemetrySourceKind.DBoxSdkSample)
        {
            return false;
        }

        if (frame.Boundary != TelemetryFrameBoundary.None)
        {
            return frame.Boundary != TelemetryFrameBoundary.ReplayComplete;
        }

        var discreteSignals =
            TelemetrySignalSet.Boost |
            TelemetrySignalSet.Impact |
            TelemetrySignalSet.LandingGear;
        return (frame.UpdatedSignals & discreteSignals) != 0 ||
               sampleFrameSequence <= 12 ||
               sampleFrameSequence % 30 == 0;
    }

    public static bool TryFormat(
        StarCitizenTelemetryFrame frame,
        out string summary)
    {
        summary = string.Empty;
        if (frame.SourceKind != TelemetrySourceKind.DBoxSdkSample)
        {
            return false;
        }

        var application = string.IsNullOrWhiteSpace(frame.ApplicationKey)
            ? "SDK sample"
            : frame.ApplicationKey;
        if (frame.Boundary != TelemetryFrameBoundary.None)
        {
            if (frame.Boundary == TelemetryFrameBoundary.ReplayComplete)
            {
                return false;
            }

            summary = $"{application} {frame.Boundary}: state neutralized";
            return true;
        }

        if (frame.UpdatedSignals == TelemetrySignalSet.None)
        {
            return false;
        }

        var values = new List<string>();
        if ((frame.UpdatedSignals & TelemetrySignalSet.EngineRumble) != 0)
        {
            var engine = $"engine {FormatPercent(frame.EngineRumble)}";
            if ((frame.UpdatedSignals & TelemetrySignalSet.EngineFrequency) != 0)
            {
                engine += $" @ {FormatNumber(frame.EngineFrequencyHz, "0")} Hz";
            }

            values.Add(engine);
        }

        if ((frame.UpdatedSignals & TelemetrySignalSet.GForce) != 0)
        {
            values.Add(
                "G " +
                $"lat {FormatNumber(frame.GForceLateral, "+0.00;-0.00;0.00")}, " +
                $"vert {FormatNumber(frame.GForceVertical, "+0.00;-0.00;0.00")}, " +
                $"long {FormatNumber(frame.GForceLongitudinal, "+0.00;-0.00;0.00")}");
        }

        if ((frame.UpdatedSignals & TelemetrySignalSet.Boost) != 0)
        {
            values.Add(frame.Boost > 0
                ? $"boost {FormatPercent(frame.Boost)}"
                : "boost off");
        }

        if ((frame.UpdatedSignals & TelemetrySignalSet.Impact) != 0)
        {
            values.Add($"impact {FormatPercent(frame.Impact)}");
        }

        if ((frame.UpdatedSignals & TelemetrySignalSet.LandingGear) != 0)
        {
            values.Add(frame.LandingGear switch
            {
                <= 0.01 => "gear retracted",
                >= 0.99 => "gear deployed",
                _ => $"gear {FormatPercent(frame.LandingGear)}"
            });
        }

        if (values.Count == 0)
        {
            return false;
        }

        summary = $"{application} {frame.RawKind ?? "frame"}: {string.Join("; ", values)}";
        return true;
    }

    private static string FormatPercent(double value) =>
        Math.Round(Clamp01(value) * 100, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string FormatNumber(double value, string format) =>
        (double.IsFinite(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

    private static double Clamp01(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
}
