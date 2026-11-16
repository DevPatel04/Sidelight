using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    internal const int RazerVendorId = 0x1532;
    internal const int RazerReportLength = 91; // 1-byte HID report ID (0x00) + 90-byte Razer frame
    internal const int RazerPaReportLength = 64;
    internal const byte RazerClassPower = 0x07;
    internal const byte RazerCmdBattery = 0x80;
    internal const byte RazerCmdCharging = 0x84;
    internal const byte RazerStatusNew = 0x00;
    internal const byte RazerStatusBusy = 0x01;
    internal const byte RazerStatusOk = 0x02;
    internal const byte RazerStatusTimeout = 0x04;
    internal const byte RazerStatusNotSupported = 0x05;

    internal const byte RazerPaReportId = 0x02;
    internal const byte RazerPaCmdBattery = 0x21;
    internal const byte RazerPaCmdCharging = 0x2A;

    private static readonly byte[] RazerTxCandidates = [0x1F, 0x3F, 0xFF];
    // 64-byte vendor-HID "PA" headsets (UsagePage 0xFF00/0xFF14, Report ID 0x02).
    private static readonly HashSet<int> RazerPaPids = [0x0536, 0x0555, 0x0556, 0x0583];

    private static readonly Dictionary<int, (string Name, string Kind)> RazerDevices = new()
    {
        // Mice (PIDs from OpenRazer razermouse_driver.h)
        [0x0045] = ("Razer Mamba Wireless", "mouse"),
        [0x006F] = ("Razer Lancehead Wireless", "mouse"),
        [0x0072] = ("Razer Mamba Wireless", "mouse"),
        [0x0077] = ("Razer Pro Click", "mouse"),
        [0x007A] = ("Razer Viper Ultimate", "mouse"),
        [0x007B] = ("Razer Viper Ultimate", "mouse"),
        [0x007C] = ("Razer DeathAdder V2 Pro", "mouse"),
        [0x007D] = ("Razer DeathAdder V2 Pro", "mouse"),
        [0x0083] = ("Razer Basilisk X HyperSpeed", "mouse"),
        [0x0086] = ("Razer Basilisk Ultimate", "mouse"),
        [0x0088] = ("Razer Basilisk Ultimate", "mouse"),
        [0x008F] = ("Razer Naga Pro", "mouse"),
        [0x0090] = ("Razer Naga Pro", "mouse"),
        [0x0094] = ("Razer Orochi V2", "mouse"),
        [0x009A] = ("Razer Pro Click Mini", "mouse"),
        [0x009C] = ("Razer DeathAdder V2 X HyperSpeed", "mouse"),
        [0x009E] = ("Razer Viper Mini SE", "mouse"),
        [0x009F] = ("Razer Viper Mini SE", "mouse"),
        [0x00A5] = ("Razer Viper V2 Pro", "mouse"),
        [0x00A6] = ("Razer Viper V2 Pro", "mouse"),
        [0x00A7] = ("Razer Naga V2 Pro", "mouse"),
        [0x00A8] = ("Razer Naga V2 Pro", "mouse"),
        [0x00AA] = ("Razer Basilisk V3 Pro", "mouse"),
        [0x00AB] = ("Razer Basilisk V3 Pro", "mouse"),
        [0x00AF] = ("Razer Cobra Pro", "mouse"),
        [0x00B0] = ("Razer Cobra Pro", "mouse"),
        [0x00B4] = ("Razer Naga V2 HyperSpeed", "mouse"),
        [0x00B6] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00B7] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00B8] = ("Razer Viper V3 HyperSpeed", "mouse"),
        [0x00B9] = ("Razer Basilisk V3 X HyperSpeed", "mouse"),
        [0x00BE] = ("Razer DeathAdder V4 Pro", "mouse"),
        [0x00BF] = ("Razer DeathAdder V4 Pro", "mouse"),
        [0x00C0] = ("Razer Viper V3 Pro", "mouse"),
        [0x00C1] = ("Razer Viper V3 Pro", "mouse"),
        [0x00C2] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00C3] = ("Razer DeathAdder V3 Pro", "mouse"),
        [0x00C4] = ("Razer DeathAdder V3 HyperSpeed", "mouse"),
        [0x00C5] = ("Razer DeathAdder V3 HyperSpeed", "mouse"),
        [0x00CC] = ("Razer Basilisk V3 Pro 35K", "mouse"),
        [0x00CD] = ("Razer Basilisk V3 Pro 35K", "mouse"),
        [0x00D0] = ("Razer Pro Click V2", "mouse"),
        [0x00D1] = ("Razer Pro Click V2", "mouse"),
        [0x00DB] = ("Razer Cobra HyperSpeed", "mouse"),
        [0x00DE] = ("Razer Viper V3 Pro SE", "mouse"),
        [0x00DF] = ("Razer Viper V3 Pro SE", "mouse"),
        [0x00E5] = ("Razer Viper V4 Pro", "mouse"),
        [0x00E6] = ("Razer Viper V4 Pro", "mouse"),
        // Keyboards (PIDs from OpenRazer razerkbd_driver.h)
        [0x0258] = ("Razer BlackWidow V3 Mini HyperSpeed", "keyboard"),
        [0x025A] = ("Razer BlackWidow V3 Pro", "keyboard"),
        [0x025C] = ("Razer BlackWidow V3 Pro", "keyboard"),
        [0x0271] = ("Razer BlackWidow V3 Mini HyperSpeed", "keyboard"),
        [0x0290] = ("Razer DeathStalker V2 Pro", "keyboard"),
        [0x0292] = ("Razer DeathStalker V2 Pro", "keyboard"),
        [0x0296] = ("Razer DeathStalker V2 Pro TKL", "keyboard"),
        [0x0298] = ("Razer DeathStalker V2 Pro TKL", "keyboard"),
        [0x02B9] = ("Razer BlackWidow V4 Mini HyperSpeed", "keyboard"),
        [0x02BA] = ("Razer BlackWidow V4 Mini HyperSpeed", "keyboard"),
        [0x02D5] = ("Razer BlackWidow V4 TKL HyperSpeed", "keyboard"),
        [0x02D7] = ("Razer BlackWidow V4 TKL HyperSpeed", "keyboard"),
        // Headsets
        [0x0528] = ("Razer BlackShark V2", "headphones"),
        [0x0536] = ("Razer Barracuda X", "headphones"),
        [0x0555] = ("Razer BlackShark V2 Pro", "headphones"),
        [0x0556] = ("Razer BlackShark V2 Pro", "headphones"),
        [0x0583] = ("Razer Barracuda Pro", "headphones"),
    };

    /// <summary>Builds a 91-byte HID Feature report querying a Razer power property (0x07 / 0x80 or 0x84).</summary>
    internal static byte[] BuildRazerReport(byte txId, byte commandId)
    {
        var buf = new byte[RazerReportLength];
        buf[0] = 0x00;
        buf[1] = RazerStatusNew;
        buf[2] = txId;
        buf[6] = 0x02;
        buf[7] = RazerClassPower;
        buf[8] = commandId;
        byte crc = 0;
        for (int i = 3; i <= 88; i++) crc ^= buf[i];
        buf[89] = crc;
        return buf;
    }

    /// <summary>Extracts battery percentage (1-100) from a 91-byte 0x07/0x80 response, or null when asleep/invalid.</summary>
    internal static int? ParseRazerBattery(ReadOnlySpan<byte> report)
    {
        if (report.Length < RazerReportLength) return null;
        if (report[1] != RazerStatusOk || report[7] != RazerClassPower || report[8] != RazerCmdBattery) return null;
        byte raw = report[10];
        // Wireless dongles report 0 when the peripheral is asleep or out of range.
        if (raw == 0) return null;
        return Math.Clamp((int)Math.Round(raw * 100.0 / 255.0), 0, 100);
    }

    /// <summary>Extracts charging state from a 91-byte 0x07/0x84 response, or null when invalid.</summary>
    internal static bool? ParseRazerCharging(ReadOnlySpan<byte> report)
    {
        if (report.Length < RazerReportLength) return null;
        if (report[1] != RazerStatusOk || report[7] != RazerClassPower || report[8] != RazerCmdCharging) return null;
        return report[10] != 0;
    }

    internal static byte[] BuildRazerPaRemoteFrame(bool on)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerPaReportId;
        buf[1] = 0x80;
        buf[2] = 0x07;
        buf[5] = (byte)'P';
        buf[6] = (byte)'A';
        buf[7] = 0x0E;
        buf[9] = 0x02;
        buf[10] = 0xE1;
        buf[11] = (byte)(on ? 1 : 0);
        return buf;
    }

    internal static byte[] BuildRazerPaQueryFrame(byte cmdId)
    {
        var buf = new byte[RazerPaReportLength];
        buf[0] = RazerPaReportId;
        buf[1] = 0x80;
        buf[2] = 0x08;
        buf[5] = (byte)'P';
        buf[6] = (byte)'A';
        buf[7] = 0x08;
        buf[9] = 0x03;
        buf[10] = cmdId;
        return buf;
    }

    internal static byte? ParseRazerPaReply(ReadOnlySpan<byte> report, byte cmdId)
    {
        if (report.Length >= 16 && report[0] == RazerPaReportId && report[12] == cmdId && report[13] == 0x01)
            return report[15];
        return null;
    }

    internal static (string? Name, string? Kind) RazerDeviceInfo(int pid)
        => RazerDevices.TryGetValue(pid, out var info) ? info : (null, null);

    internal static string? RazerKindFromPid(int pid) => RazerDeviceInfo(pid).Kind;

    internal static List<DeviceBattery> MergeRazerDevices(IEnumerable<(string PhysicalId, DeviceBattery Device)> readings)
    {
        var byPhysicalId = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, dev) in readings)
        {
            if (!byPhysicalId.TryGetValue(id, out var existing) || PreferDevice(dev, existing))
                byPhysicalId[id] = dev;
        }

        // Disambiguate distinct physical devices that share the same product name (e.g. two identical receivers)
        // so MergeDevices preserves every physical peripheral.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceBattery>(byPhysicalId.Count);
        foreach (var dev in byPhysicalId.Values)
        {
            var n = counts.TryGetValue(dev.Name, out var c) ? c + 1 : 1;
            counts[dev.Name] = n;
            result.Add(n == 1 ? dev : dev with { Name = $"{dev.Name} ({n})" });
        }
        return result;
    }

    internal static List<DeviceBattery> MergeDevices(IEnumerable<DeviceBattery> primary, IEnumerable<DeviceBattery> extra)
    {
        var byName = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in primary.Concat(extra))
        {
            if (!byName.TryGetValue(d.Name, out var existing) || PreferDevice(d, existing))
                byName[d.Name] = d;
        }
        return byName.Values.ToList();
    }

    private static bool PreferDevice(DeviceBattery candidate, DeviceBattery existing)
        => (candidate.Charging is not null && existing.Charging is null)
            || (candidate.Charging == true && existing.Charging != true)
            || (candidate.Charging == existing.Charging && candidate.Level > existing.Level);

#if WINDOWS
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private static List<DeviceBattery> WindowsRazer()
    {
        var readings = new List<(string PhysicalId, DeviceBattery Device)>();
        var handledDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var guid = HidInterfaceGuid;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return [];

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var req, IntPtr.Zero);
                    if (req == 0) continue;

                    var mem = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, mem, req, out _, ref devData)) continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(mem, 4));
                        if (string.IsNullOrEmpty(path) || !path.Contains("vid_1532", StringComparison.OrdinalIgnoreCase)) continue;

                        var physicalId = GetPhysicalDeviceId(path, devData.DevInst);
                        if (handledDevices.Contains(physicalId)) continue;

                        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid) continue;

                        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != RazerVendorId) continue;

                        int pid = attrs.ProductID;
                        if (!TryGetHidCaps(handle, out var caps)) continue;

                        DeviceBattery? dev = null;
                        if (RazerPaPids.Contains(pid) && caps.UsagePage is 0xFF00 or 0xFF14
                            && caps.InputReportByteLength == RazerPaReportLength
                            && caps.OutputReportByteLength == RazerPaReportLength)
                            dev = ReadRazerPaDevice(path, handle, pid);
                        else if (caps.FeatureReportByteLength == RazerReportLength)
                            dev = ReadRazerDevice(handle, pid);

                        if (dev is not null)
                        {
                            handledDevices.Add(physicalId);
                            readings.Add((physicalId, dev));
                        }
                    }
                    catch { }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch { }
        return MergeRazerDevices(readings);
    }

    private static string GetPhysicalDeviceId(string path, uint devInst)
    {
        // Walk up from HID\... to the parent USB device node so multiple interfaces on one receiver share one ID,
        // while two identical receivers on different USB ports stay distinct.
        uint current = devInst;
        for (int hop = 0; hop < 3; hop++)
        {
            if (CM_Get_Parent(out var parent, current, 0) != 0) break;
            current = parent;
            var buf = new char[256];
            if (CM_Get_Device_IDW(current, buf, buf.Length, 0) == 0)
            {
                var id = new string(buf).TrimEnd('\0');
                if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                    && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        return devInst != 0 ? $"devinst:{devInst}" : path;
    }

    private static bool TryGetHidCaps(SafeFileHandle handle, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out var pp)) return false;
        try { return HidP_GetCaps(pp, out caps) >= 0; }
        finally { HidD_FreePreparsedData(pp); }
    }

    private static (string Name, string? Kind) ResolveRazerNameAndKind(SafeFileHandle handle, int pid)
    {
        var (knownName, knownKind) = RazerDeviceInfo(pid);
        var sb = new StringBuilder(256);
        var name = HidD_GetProductString(handle, sb, (uint)(sb.Capacity * 2)) && sb.Length > 0
            ? sb.ToString().Trim()
            : knownName ?? $"Razer Device ({pid:X4})";
        return (name, knownKind);
    }

    private static DeviceBattery? ReadRazerDevice(SafeFileHandle handle, int pid)
    {
        foreach (var tx in RazerTxCandidates)
        {
            var batResp = QueryRazerFeature(handle, tx, RazerCmdBattery);
            if (batResp is null) continue;
            if (batResp[1] == RazerStatusTimeout) break;
            if (batResp[1] == RazerStatusNotSupported) continue;

            var level = ParseRazerBattery(batResp);
            if (level is null) continue;

            var chgResp = QueryRazerFeature(handle, tx, RazerCmdCharging);
            var charging = chgResp is not null ? ParseRazerCharging(chgResp) : null;

            var (name, knownKind) = ResolveRazerNameAndKind(handle, pid);
            return new DeviceBattery(name, level.Value, knownKind ?? KindOf(name), charging);
        }
        return null;
    }

    private static DeviceBattery? ReadRazerPaDevice(string path, SafeFileHandle infoHandle, int pid)
    {
        // GENERIC_READ | GENERIC_WRITE (0xC0000000), FILE_SHARE_READ | FILE_SHARE_WRITE (3), FILE_FLAG_OVERLAPPED (0x40000000)
        using var rwHandle = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (rwHandle.IsInvalid) return null;

        for (int i = 0; i < 16; i++)
            if (ReadPaOverlapped(rwHandle, RazerPaReportLength, 10) is null) break;

        if (!WritePaOverlapped(rwHandle, BuildRazerPaRemoteFrame(true), 200)) return null;
        Thread.Sleep(35);

        try
        {
            var bat = QueryRazerPa(rwHandle, RazerPaCmdBattery);
            if (bat is null || bat.Value > 100) return null;
            var chg = QueryRazerPa(rwHandle, RazerPaCmdCharging);
            bool? charging = chg is not null ? chg.Value != 0 : null;

            var (name, knownKind) = ResolveRazerNameAndKind(infoHandle, pid);
            return new DeviceBattery(name, bat.Value, knownKind ?? KindOf(name), charging);
        }
        finally
        {
            WritePaOverlapped(rwHandle, BuildRazerPaRemoteFrame(false), 150);
        }
    }

    private static byte? QueryRazerPa(SafeFileHandle handle, byte cmdId)
    {
        var query = BuildRazerPaQueryFrame(cmdId);
        var remote = BuildRazerPaRemoteFrame(true);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!WritePaOverlapped(handle, remote, 200)) break;
            Thread.Sleep(35);
            if (!WritePaOverlapped(handle, query, 200)) break;

            var deadline = Environment.TickCount64 + 180;
            while (Environment.TickCount64 < deadline)
            {
                var reply = ReadPaOverlapped(handle, RazerPaReportLength, 50);
                if (reply is null) continue;
                var val = ParseRazerPaReply(reply, cmdId);
                if (val is not null) return val;
            }
        }
        return null;
    }

    private static bool WritePaOverlapped(SafeFileHandle handle, byte[] data, int timeoutMs)
        => PaOverlappedIo(isWrite: true, handle, data, timeoutMs) == data.Length;

    private static byte[]? ReadPaOverlapped(SafeFileHandle handle, int len, int timeoutMs)
    {
        var buf = new byte[len];
        var read = PaOverlappedIo(isWrite: false, handle, buf, timeoutMs);
        if (read <= 0) return null;
        if (read < len) Array.Resize(ref buf, read);
        return buf;
    }

    private static int PaOverlappedIo(bool isWrite, SafeFileHandle handle, byte[] buf, int timeoutMs)
    {
        var ev = CreateEventW(IntPtr.Zero, true, false, null);
        if (ev == IntPtr.Zero) return -1;
        var pinBuf = GCHandle.Alloc(buf, GCHandleType.Pinned);
        var ovMem = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
        try
        {
            Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ovMem, false);
            var ok = isWrite
                ? WriteFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, out var transferred, ovMem)
                : ReadFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, out transferred, ovMem);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != 997) return -1; // ERROR_IO_PENDING
                if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    // Wait for overlapped cancellation before unpinning the buffer.
                    CancelIoEx(handle, ovMem);
                    GetOverlappedResult(handle, ovMem, out _, true);
                    return -1;
                }
                if (!GetOverlappedResult(handle, ovMem, out transferred, false)) return -1;
            }
            return (int)transferred;
        }
        finally
        {
            Marshal.FreeHGlobal(ovMem);
            pinBuf.Free();
            CloseHandle(ev);
        }
    }

    private static byte[]? QueryRazerFeature(SafeFileHandle handle, byte txId, byte commandId)
    {
        var req = BuildRazerReport(txId, commandId);
        var resp = new byte[RazerReportLength];
        var deadline = Environment.TickCount64 + 180;

        for (int attempt = 0; attempt < 3 && Environment.TickCount64 < deadline; attempt++)
        {
            if (!HidD_SetFeature(handle, req, (uint)req.Length)) return null;
            Thread.Sleep(5);

            for (int poll = 0; poll < 3 && Environment.TickCount64 < deadline; poll++)
            {
                Array.Clear(resp);
                if (!HidD_GetFeature(handle, resp, (uint)resp.Length)) return null;
                byte status = resp[1];
                if (status is RazerStatusTimeout or RazerStatusNotSupported) return resp;
                if (status == RazerStatusOk && resp[7] == RazerClassPower && resp[8] == commandId) return resp;
                if (status == RazerStatusBusy) { Thread.Sleep(25); continue; }
                Thread.Sleep(15);
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED { public IntPtr Internal; public IntPtr InternalHigh; public int Offset; public int OffsetHigh; public IntPtr hEvent; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, IntPtr data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA data);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buf, uint len, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buf, uint len, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr sec, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buf, uint len);
#endif
}
