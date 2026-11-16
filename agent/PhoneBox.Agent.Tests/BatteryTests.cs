using PhoneBox.Agent;
using Xunit;

public class BatteryTests
{
    [Theory]
    [InlineData("WH-1000XM5", "headphones")]
    [InlineData("Galaxy Buds2 Pro", "headphones")]
    [InlineData("AirPods Pro", "headphones")]
    [InlineData("Razer BlackShark V2 Pro 2.4", "headphones")]
    [InlineData("Razer Barracuda X", "headphones")]
    [InlineData("MX Master 3S", "mouse")]
    [InlineData("Logitech Pebble M350", "mouse")]
    [InlineData("Razer Basilisk V3 Pro", "mouse")]
    [InlineData("Razer Viper V3 Pro", "mouse")]
    [InlineData("Razer Lancehead Wireless", "mouse")]
    [InlineData("MX Keys", "keyboard")]
    [InlineData("Slim Solar+", "keyboard")]
    [InlineData("Razer BlackWidow V4 Pro", "keyboard")]
    [InlineData("Xbox Wireless Controller", "controller")]
    [InlineData("DualSense Wireless Controller", "controller")]
    [InlineData("Some Speaker", "other")]
    public void KindFromName(string name, string kind) => Assert.Equal(kind, BatteryMonitor.KindOf(name));

    [Fact]
    public void BuildRazerReport_ConstructsValidBatteryAndChargingRequests()
    {
        var batteryReq = BatteryMonitor.BuildRazerReport(0x1F, BatteryMonitor.RazerCmdBattery);
        Assert.Equal(BatteryMonitor.RazerReportLength, batteryReq.Length);
        Assert.Equal(0x00, batteryReq[0]);
        Assert.Equal(BatteryMonitor.RazerStatusNew, batteryReq[1]);
        Assert.Equal(0x1F, batteryReq[2]);
        Assert.Equal(0x02, batteryReq[6]);
        Assert.Equal(BatteryMonitor.RazerClassPower, batteryReq[7]);
        Assert.Equal(BatteryMonitor.RazerCmdBattery, batteryReq[8]);
        // CRC is XOR of Razer frame bytes [2..87] (HID buffer indices [3..88]) = 0x02 ^ 0x07 ^ 0x80 = 0x85.
        Assert.Equal(0x85, batteryReq[89]);

        var chargingReq = BatteryMonitor.BuildRazerReport(0x3F, BatteryMonitor.RazerCmdCharging);
        Assert.Equal(BatteryMonitor.RazerReportLength, chargingReq.Length);
        Assert.Equal(0x3F, chargingReq[2]);
        Assert.Equal(0x02, chargingReq[6]);
        Assert.Equal(BatteryMonitor.RazerClassPower, chargingReq[7]);
        Assert.Equal(BatteryMonitor.RazerCmdCharging, chargingReq[8]);
        // CRC = 0x02 ^ 0x07 ^ 0x84 = 0x81.
        Assert.Equal(0x81, chargingReq[89]);
    }

    [Fact]
    public void ParseRazerBattery_And_Charging_FromCapturedDeviceResponses()
    {
        // Real 91-byte HidD_GetFeature responses captured from Razer Basilisk V3 Pro (PID 0x00AB, wireless).
        byte[] capturedBattery = Convert.FromHexString("00021F0000000207800039" + new string('0', 78 * 2) + "BC00");
        byte[] capturedCharging = Convert.FromHexString("00021F0000000207840000" + new string('0', 78 * 2) + "8100");

        Assert.Equal(22, BatteryMonitor.ParseRazerBattery(capturedBattery));
        Assert.False(BatteryMonitor.ParseRazerCharging(capturedCharging));

        // Real 91-byte battery response shape from an earlier wireless session (0x78 = 47%) and full-charge (0xFF = 100%).
        byte[] midBattery = Convert.FromHexString("00021F0000000207800078" + new string('0', 78 * 2) + "FD00");
        byte[] fullBatteryTx3F = Convert.FromHexString("00023F00000002078000FF" + new string('0', 78 * 2) + "7A00");
        byte[] activeCharging = Convert.FromHexString("00023F0000000207840001" + new string('0', 78 * 2) + "8000");

        Assert.Equal(47, BatteryMonitor.ParseRazerBattery(midBattery));
        Assert.Equal(100, BatteryMonitor.ParseRazerBattery(fullBatteryTx3F));
        Assert.True(BatteryMonitor.ParseRazerCharging(activeCharging));

        // Edge cases: sleeping/off (raw=0), busy (status=0x01), foreign command class (0x0F), short buffer.
        byte[] sleeping = Convert.FromHexString("00021F0000000207800000" + new string('0', 78 * 2) + "8500");
        byte[] busy = Convert.FromHexString("00011F0000000207800078" + new string('0', 78 * 2) + "FD00");
        byte[] foreign = Convert.FromHexString("00021F0000002C0F030000" + new string('0', 78 * 2) + "0000");

        Assert.Null(BatteryMonitor.ParseRazerBattery(sleeping));
        Assert.Null(BatteryMonitor.ParseRazerBattery(busy));
        Assert.Null(BatteryMonitor.ParseRazerBattery(foreign));
        Assert.Null(BatteryMonitor.ParseRazerCharging(foreign));
        Assert.Null(BatteryMonitor.ParseRazerBattery(new byte[16]));
        Assert.Null(BatteryMonitor.ParseRazerCharging(new byte[16]));
    }

    [Fact]
    public void RazerPaProtocol_BuildsFramesAndParsesCapturedHeadsetResponses()
    {
        var remoteOn = BatteryMonitor.BuildRazerPaRemoteFrame(true);
        Assert.Equal(BatteryMonitor.RazerPaReportLength, remoteOn.Length);
        Assert.Equal(BatteryMonitor.RazerPaReportId, remoteOn[0]);
        Assert.Equal((byte)'P', remoteOn[5]);
        Assert.Equal((byte)'A', remoteOn[6]);
        Assert.Equal(0xE1, remoteOn[10]);
        Assert.Equal(0x01, remoteOn[11]);

        var remoteOff = BatteryMonitor.BuildRazerPaRemoteFrame(false);
        Assert.Equal(0x00, remoteOff[11]);

        var query = BatteryMonitor.BuildRazerPaQueryFrame(BatteryMonitor.RazerPaCmdBattery);
        Assert.Equal(BatteryMonitor.RazerPaReportLength, query.Length);
        Assert.Equal(BatteryMonitor.RazerPaReportId, query[0]);
        Assert.Equal(BatteryMonitor.RazerPaCmdBattery, query[10]);

        // Real 64-byte PA replies captured from Razer BlackShark V2 Pro 2.4 (PID 0x0555, wireless).
        byte[] capturedPaBattery = Convert.FromHexString(
            "020E504908C8ED6B510004002101011D" +
            "504908C683FB4C0004002A0201000002" +
            "060B0002060B" +
            new string('0', 26 * 2));
        byte[] capturedPaCharging = Convert.FromHexString(
            "020E504908C9126C510004002A010100" +
            "504908C683FB4C0004002A0201000002" +
            "060B0002060B" +
            new string('0', 26 * 2));

        Assert.Equal((byte)29, BatteryMonitor.ParseRazerPaReply(capturedPaBattery, BatteryMonitor.RazerPaCmdBattery));
        Assert.Null(BatteryMonitor.ParseRazerPaReply(capturedPaBattery, BatteryMonitor.RazerPaCmdCharging));
        Assert.Equal((byte)0, BatteryMonitor.ParseRazerPaReply(capturedPaCharging, BatteryMonitor.RazerPaCmdCharging));
        Assert.Null(BatteryMonitor.ParseRazerPaReply(new byte[10], BatteryMonitor.RazerPaCmdBattery));
    }

    [Theory]
    [InlineData(0x00AB, "mouse")]
    [InlineData(0x00C1, "mouse")]
    [InlineData(0x0555, "headphones")]
    [InlineData(0x025C, "keyboard")]
    [InlineData(0xFFFF, null)]
    public void RazerKindFromPid_MapsKnownDevicesAndFallsBackToName(int pid, string? expectedKind)
        => Assert.Equal(expectedKind, BatteryMonitor.RazerKindFromPid(pid));

    [Fact]
    public void MergeDevices_DeduplicatesAndPrefersChargingMetadata()
    {
        var bluetooth = new[] { new DeviceBattery("Razer Basilisk V3 Pro", 45, "mouse", null) };
        var hid = new[]
        {
            new DeviceBattery("Razer Basilisk V3 Pro", 47, "mouse", true),
            new DeviceBattery("Razer BlackWidow V3 Pro", 80, "keyboard", false),
        };

        var merged = BatteryMonitor.MergeDevices(bluetooth, hid);
        Assert.Equal(2, merged.Count);
        var mouse = Assert.Single(merged, d => d.Name == "Razer Basilisk V3 Pro");
        Assert.True(mouse.Charging);
        Assert.Equal(47, mouse.Level);
    }
}
