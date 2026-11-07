using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NteOmikuji.Core.Capture;

public enum CaptureBackendKind
{
    Auto,
    Npcap,
    RawSocket,
}

public class CaptureUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Npcap is optional, so its absence is normal rather than a fallback worth reporting.</summary>
public sealed class NpcapNotInstalledException(Exception inner) : CaptureUnavailableException("找不到 Npcap。", inner);

/// <summary>Raw capture was refused because this process is not elevated.</summary>
public sealed class CaptureRequiresElevationException(Exception inner)
    : CaptureUnavailableException("原始封包擷取需要系統管理員權限。", inner);

public sealed record CaptureStatistics(uint Received, uint Dropped, uint InterfaceDropped);

public interface IPacketCapture : IDisposable
{
    string Name { get; }
    string Detail { get; }
    string? FallbackReason { get; }

    /// <summary>Blocks briefly for the next packet. Null means nothing arrived, so the caller can check for cancellation.</summary>
    IpPacket? Read();

    CaptureStatistics? Statistics();
}

public sealed record LocalAddress(string Ip, string InterfaceName);

public static class LocalAddresses
{
    public static IReadOnlyList<LocalAddress> List() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => new LocalAddress(address.Address.ToString(), adapter.Name)))
            .ToList();

    /// <summary>Finds the default route's address. A UDP connect only selects a route and sends nothing.</summary>
    public static string? Preferred()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(IPAddress.Parse("1.1.1.1"), 80);
            if (probe.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address))
            {
                return address.ToString();
            }
        }
        catch (SocketException)
        {
            // Fall through to the adapter list when there is no default route.
        }
        return List().FirstOrDefault()?.Ip;
    }
}

public static class Ipv4Parser
{
    public static IpPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28 || data[0] >> 4 != 4)
        {
            return null;
        }
        var headerLength = (data[0] & 0x0F) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (totalLength > 0 && totalLength < data.Length)
        {
            data = data[..totalLength];
        }
        if (headerLength < 20 || data.Length < headerLength + 8)
        {
            return null;
        }
        var source = new IPAddress(data.Slice(12, 4)).ToString();
        var destination = new IPAddress(data.Slice(16, 4)).ToString();
        var transport = data[headerLength..];
        int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(transport);
        int destinationPort = BinaryPrimitives.ReadUInt16BigEndian(transport[2..]);
        switch (data[9])
        {
            case 17:
                int udpLength = BinaryPrimitives.ReadUInt16BigEndian(transport[4..]);
                var udpEnd = Math.Clamp(udpLength, 8, transport.Length);
                return new IpPacket(source, destination, sourcePort, destinationPort, transport[8..udpEnd].ToArray(), false);
            case 6:
                if (transport.Length < 20)
                {
                    return null;
                }
                var tcpHeader = (transport[12] >> 4) * 4;
                if (tcpHeader < 20 || transport.Length < tcpHeader)
                {
                    return null;
                }
                return new IpPacket(source, destination, sourcePort, destinationPort, transport[tcpHeader..].ToArray(), true,
                    BinaryPrimitives.ReadUInt32BigEndian(transport[4..]));
            default:
                return null;
        }
    }
}

[SupportedOSPlatform("windows")]
public static class CaptureFactory
{
    public static IPacketCapture Open(string localIp, CaptureBackendKind requested)
    {
        if (requested == CaptureBackendKind.RawSocket)
        {
            return new RawSocketCapture(localIp, null);
        }
        try
        {
            return new NpcapCapture(localIp);
        }
        catch (CaptureUnavailableException exception) when (requested == CaptureBackendKind.Auto)
        {
            return new RawSocketCapture(localIp, exception is NpcapNotInstalledException ? null : exception.Message);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class RawSocketCapture : IPacketCapture
{
    private const int ReceiveBufferSize = 16 * 1024 * 1024;

    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[65535];

    public RawSocketCapture(string localIp, string? fallbackReason)
    {
        Detail = localIp;
        FallbackReason = fallbackReason;
        Socket? socket = null;
        try
        {
            // Without Administrator rights the constructor itself is what fails.
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            socket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
            socket.ReceiveBufferSize = ReceiveBufferSize;
            socket.ReceiveTimeout = 500;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
            socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
            _socket = socket;
        }
        catch (SocketException exception)
        {
            socket?.Dispose();
            if (exception.SocketErrorCode == SocketError.AccessDenied)
            {
                throw new CaptureRequiresElevationException(exception);
            }
            throw new CaptureUnavailableException($"無法開啟原始封包擷取：{exception.Message}", exception);
        }
    }

    public string Name => "windows_packet";
    public string Detail { get; }
    public string? FallbackReason { get; }

    public IpPacket? Read()
    {
        try
        {
            var length = _socket.Receive(_buffer);
            return Ipv4Parser.Parse(_buffer.AsSpan(0, length));
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
        {
            return null;
        }
    }

    public CaptureStatistics? Statistics() => null;

    public void Dispose()
    {
        try
        {
            _socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(0), new byte[4]);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // The socket is closing either way.
        }
        _socket.Dispose();
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class NpcapCapture : IPacketCapture
{
    private const string Library = "wpcap";
    private const int SnapLength = 65535;
    private const int BufferSize = 16 * 1024 * 1024;
    private const int ReadTimeoutMs = 100;
    private const int ErrorBufferSize = 256;
    private const int DltNull = 0;
    private const int DltEthernet = 1;
    private const int DltRawBsd = 12;
    private const int DltRaw = 101;
    private const int DltLoop = 108;

    private readonly nint _handle;
    private readonly int _datalink;
    private bool _disposed;

    static NpcapCapture()
    {
        NativeLibrary.SetDllImportResolver(typeof(NpcapCapture).Assembly, (name, _, _) =>
            name == Library ? LoadFromSystemDirectory() : 0);
    }

    public NpcapCapture(string localIp)
    {
        try
        {
            Detail = FindDevice(localIp);
        }
        catch (DllNotFoundException exception)
        {
            throw new NpcapNotInstalledException(exception);
        }

        var error = new byte[ErrorBufferSize];
        _handle = pcap_create(Detail, error);
        if (_handle == 0)
        {
            throw new CaptureUnavailableException(ErrorText(error));
        }
        try
        {
            pcap_set_snaplen(_handle, SnapLength);
            pcap_set_promisc(_handle, 0);
            pcap_set_timeout(_handle, ReadTimeoutMs);
            pcap_set_buffer_size(_handle, BufferSize);
            if (pcap_activate(_handle) < 0)
            {
                throw new CaptureUnavailableException(LastError());
            }
            var program = default(BpfProgram);
            if (pcap_compile(_handle, ref program, $"host {IPAddress.Parse(localIp)} and (udp or tcp)", 1, 0xFFFFFFFF) != 0)
            {
                throw new CaptureUnavailableException(LastError());
            }
            try
            {
                if (pcap_setfilter(_handle, ref program) != 0)
                {
                    throw new CaptureUnavailableException(LastError());
                }
            }
            finally
            {
                pcap_freecode(ref program);
            }
            _datalink = pcap_datalink(_handle);
        }
        catch
        {
            pcap_close(_handle);
            throw;
        }
    }

    public string Name => "npcap";
    public string Detail { get; }
    public string? FallbackReason => null;

    public IpPacket? Read()
    {
        var result = pcap_next_ex(_handle, out var header, out var data);
        if (result == 0 || result == -2)
        {
            return null;
        }
        if (result < 0)
        {
            throw new IOException(LastError());
        }
        // pcap_pkthdr on Windows: 32-bit timeval, then caplen.
        var captured = Marshal.ReadInt32(header, 8);
        unsafe
        {
            var frame = new ReadOnlySpan<byte>((void*)data, captured);
            return Ipv4Frame(frame, _datalink) is { IsEmpty: false } ip ? Ipv4Parser.Parse(ip) : null;
        }
    }

    public CaptureStatistics? Statistics()
    {
        var stats = default(PcapStat);
        return pcap_stats(_handle, ref stats) == 0 ? new CaptureStatistics(stats.Received, stats.Dropped, stats.InterfaceDropped) : null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            pcap_close(_handle);
        }
    }

    /// <summary>
    /// Loads only from System32 by absolute path, using a location that does not
    /// come from environment variables. A bare "wpcap.dll" would also search the
    /// application folder, where a planted DLL could run with capture rights.
    /// </summary>
    private static nint LoadFromSystemDirectory()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var path in new[] { Path.Combine(system, "Npcap", "wpcap.dll"), Path.Combine(system, "wpcap.dll") })
        {
            if (File.Exists(path)
                && NativeLibrary.TryLoad(path, typeof(NpcapCapture).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32, out var handle))
            {
                return handle;
            }
        }
        throw new DllNotFoundException("Npcap is not installed");
    }

    private static string FindDevice(string localIp)
    {
        var error = new byte[ErrorBufferSize];
        if (pcap_findalldevs(out var devices, error) != 0)
        {
            throw new CaptureUnavailableException(ErrorText(error));
        }
        try
        {
            for (var device = devices; device != 0; device = Marshal.ReadIntPtr(device))
            {
                var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(device, nint.Size));
                for (var address = Marshal.ReadIntPtr(device, nint.Size * 3); address != 0; address = Marshal.ReadIntPtr(address))
                {
                    var socketAddress = Marshal.ReadIntPtr(address, nint.Size);
                    if (name is not null && socketAddress != 0 && Marshal.ReadInt16(socketAddress) == (short)AddressFamily.InterNetwork)
                    {
                        var bytes = new byte[4];
                        Marshal.Copy(socketAddress + 4, bytes, 0, 4);
                        if (new IPAddress(bytes).ToString() == localIp)
                        {
                            return name;
                        }
                    }
                }
            }
        }
        finally
        {
            pcap_freealldevs(devices);
        }
        throw new CaptureUnavailableException($"沒有 Npcap 裝置擁有本機位址 {localIp}。");
    }

    private static ReadOnlySpan<byte> Ipv4Frame(ReadOnlySpan<byte> frame, int datalink)
    {
        switch (datalink)
        {
            case DltEthernet:
                if (frame.Length < 14)
                {
                    return default;
                }
                var offset = 14;
                int etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
                while (etherType is 0x8100 or 0x88A8 or 0x9100)
                {
                    if (frame.Length < offset + 4)
                    {
                        return default;
                    }
                    etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
                    offset += 4;
                }
                return etherType == 0x0800 ? frame[offset..] : default;
            case DltRawBsd or DltRaw:
                return frame;
            case DltNull or DltLoop:
                if (frame.Length < 4)
                {
                    return default;
                }
                var isIpv4 = BinaryPrimitives.ReadUInt32LittleEndian(frame) == 2 || BinaryPrimitives.ReadUInt32BigEndian(frame) == 2;
                return isIpv4 ? frame[4..] : default;
            default:
                return default;
        }
    }

    private string LastError() => Marshal.PtrToStringUTF8(pcap_geterr(_handle)) ?? "unknown Npcap error";

    private static string ErrorText(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BpfProgram
    {
        public uint Length;
        public nint Instructions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PcapStat
    {
        public uint Received;
        public uint Dropped;
        public uint InterfaceDropped;
        public uint Captured;
    }

    [LibraryImport(Library)] private static partial int pcap_findalldevs(out nint devices, [Out] byte[] error);
    [LibraryImport(Library)] private static partial void pcap_freealldevs(nint devices);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] private static partial nint pcap_create(string device, [Out] byte[] error);
    [LibraryImport(Library)] private static partial int pcap_set_snaplen(nint handle, int value);
    [LibraryImport(Library)] private static partial int pcap_set_promisc(nint handle, int value);
    [LibraryImport(Library)] private static partial int pcap_set_timeout(nint handle, int value);
    [LibraryImport(Library)] private static partial int pcap_set_buffer_size(nint handle, int value);
    [LibraryImport(Library)] private static partial int pcap_activate(nint handle);
    [LibraryImport(Library)] private static partial void pcap_close(nint handle);
    [LibraryImport(Library)] private static partial nint pcap_geterr(nint handle);
    [LibraryImport(Library)] private static partial int pcap_datalink(nint handle);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] private static partial int pcap_compile(nint handle, ref BpfProgram program, string filter, int optimize, uint netmask);
    [LibraryImport(Library)] private static partial int pcap_setfilter(nint handle, ref BpfProgram program);
    [LibraryImport(Library)] private static partial void pcap_freecode(ref BpfProgram program);
    [LibraryImport(Library)] private static partial int pcap_next_ex(nint handle, out nint header, out nint data);
    [LibraryImport(Library)] private static partial int pcap_stats(nint handle, ref PcapStat stats);
}
