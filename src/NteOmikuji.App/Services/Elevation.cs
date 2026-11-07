using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using NteOmikuji.Core.Capture;

namespace NteOmikuji.App.Services;

public sealed record PendingCapture(string LocalIp, CaptureBackendKind Backend, bool RecordPackets);

/// <summary>
/// The app runs unelevated and only asks for Administrator when built-in capture
/// needs it, by restarting itself through the normal UAC prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    private const string CaptureArgument = "--capture";
    private const string RecordPacketsArgument = "--record-packets";
    private const int ErrorCancelled = 1223;

    private static bool _pendingTaken;

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Returns false when the user declines the UAC prompt.</summary>
    public static bool RelaunchForCapture(string localIp, CaptureBackendKind backend, bool recordPackets)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{CaptureArgument} {IPAddress.Parse(localIp)} {backend}{(recordPackets ? $" {RecordPacketsArgument}" : "")}",
            });
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>Returns the capture requested on the command line, once.</summary>
    public static PendingCapture? TakePendingCapture()
    {
        if (_pendingTaken)
        {
            return null;
        }
        _pendingTaken = true;
        return Parse(Environment.GetCommandLineArgs());
    }

    private static PendingCapture? Parse(string[] arguments)
    {
        var index = Array.IndexOf(arguments, CaptureArgument);
        if (index < 0 || index + 2 >= arguments.Length)
        {
            return null;
        }
        return IPAddress.TryParse(arguments[index + 1], out var address)
            && Enum.TryParse<CaptureBackendKind>(arguments[index + 2], out var backend)
            && Enum.IsDefined(backend)
                ? new PendingCapture(address.ToString(), backend, arguments.Contains(RecordPacketsArgument))
                : null;
    }
}
