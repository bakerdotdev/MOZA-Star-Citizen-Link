using MozaStarCitizen.App.ForceFeedback.DirectInput;

namespace MozaStarCitizen.App.ForceFeedback;

public static class ForceFeedbackDiagnostics
{
    public static IReadOnlyList<string> GetLines(
        IForceFeedbackDevice selectedDevice,
        bool includeExtendedDiagnostics)
    {
        var lines = new List<string>
        {
            $"Output mode: {Environment.GetEnvironmentVariable("MOZA_SC_OUTPUT") ?? "Auto"}",
            $"Selected output: {selectedDevice.Name}",
            $"Output status: {selectedDevice.Status}"
        };
        lines.Add("AB6 output path: Windows DirectInput force feedback.");
        lines.Add("MOZA wheelbase SDK output path: removed from active selection because it does not control the AB6.");

        if (!includeExtendedDiagnostics)
        {
            lines.Add("Press Refresh to probe DirectInput controllers.");
            return lines;
        }

        try
        {
            var controllers = DirectInputNative.EnumerateGameControllers();
            var forceFeedbackDevices = DirectInputNative.EnumerateForceFeedbackDevices();
            var forceFeedbackIds = forceFeedbackDevices
                .Select(d => d.InstanceGuid)
                .ToHashSet();

            lines.Add($"DirectInput game controllers: {controllers.Count}");
            if (controllers.Count == 0)
            {
                lines.Add("  No attached DirectInput game controllers were reported by Windows.");
            }

            foreach (var controller in controllers)
            {
                var supportsForceFeedback = forceFeedbackIds.Contains(controller.InstanceGuid);
                lines.Add($"  {(supportsForceFeedback ? "[FFB]" : "[no FFB]")} {DisplayName(controller)}");
            }

            lines.Add($"DirectInput force-feedback devices: {forceFeedbackDevices.Count}");
            foreach (var device in forceFeedbackDevices)
            {
                lines.Add($"  {DisplayName(device)}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"DirectInput diagnostics failed: {ex.Message}");
        }

        return lines;
    }

    private static string DisplayName(DirectInputDeviceInfo deviceInfo)
    {
        if (!string.IsNullOrWhiteSpace(deviceInfo.ProductName))
        {
            return deviceInfo.ProductName;
        }

        return string.IsNullOrWhiteSpace(deviceInfo.InstanceName)
            ? deviceInfo.InstanceGuid.ToString()
            : deviceInfo.InstanceName;
    }
}
