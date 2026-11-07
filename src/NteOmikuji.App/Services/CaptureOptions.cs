using NteOmikuji.Core.Capture;

namespace NteOmikuji.App.Services;

/// <summary>The capture form's choices, kept outside its view so they survive moving between pages.</summary>
public sealed class CaptureOptions
{
    public string? LocalIp { get; set; }
    public CaptureBackendKind Backend { get; set; } = CaptureBackendKind.Auto;
    public bool RecordPackets { get; set; }
}
