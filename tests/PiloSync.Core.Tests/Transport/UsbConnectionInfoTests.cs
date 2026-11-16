using PiloSync.Usb;
using Xunit;

namespace PiloSync.Core.Tests;

public class UsbConnectionInfoTests
{
    [Fact]
    public void LegacySingleHotSync()
    {
        var info = new UsbConnectionInfo(0x222424, [1, 0, 2, 2]);
        var port = info.HotSyncPort();
        Assert.Equal("hotsync", port.FunctionId);
        Assert.Equal(2, port.InEndpoint);
        Assert.Equal(2, port.OutEndpoint);
    }

    [Fact]
    public void LegacyMultiplePortsWithDriverPadding()
    {
        byte[] raw = new byte[18];
        new byte[] { 2, 0, 1, 1, 2, 2 }.CopyTo(raw, 0);
        var info = new UsbConnectionInfo(0x222424, raw);
        Assert.Equal("debugger", info.Ports[0].FunctionId);
        Assert.Equal(2, info.HotSyncPort().Port);
    }

    [Fact]
    public void LegacyWithoutHotSync()
    {
        var info = new UsbConnectionInfo(0x222424, [1, 0, 0, 1]);
        Assert.True(Assert.Throws<UsbOpenException>(() => info.HotSyncPort()).IsNonHotSync);
    }

    [Fact]
    public void ChargingZ22IsNonHotSync()
    {
        var info = new UsbConnectionInfo(0x222000, [1, 1, 0, 0, 0x47, 0x72, 0x68, 0x63, 1, 0x11, 0, 0]);
        Assert.Equal("Grhc", info.Ports[0].FunctionId);
        var error = Assert.Throws<UsbOpenException>(() => info.HotSyncPort());
        Assert.True(error.IsNonHotSync);
        Assert.Null(error.Win32Error);
    }

    [Fact]
    public void InvalidHotSyncEndpointIsStillAnError()
    {
        var info = new UsbConnectionInfo(0x222424, [1, 0, 2, 0]);
        Assert.IsNotType<UsbOpenException>(Assert.Throws<IOException>(() => info.HotSyncPort()));
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 2 })]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 9, 0, 2, 1 })]
    public void LegacyRejectsInvalidLengthOrCount(byte[] raw) =>
        Assert.Throws<IOException>(() => new UsbConnectionInfo(0x222424, raw));

    [Fact]
    public void ExtendedPackedEndpointsUnchanged()
    {
        var info = new UsbConnectionInfo(0x222000, [1, 1, 0, 0, 0x63, 0x6e, 0x79, 0x73, 0, 0x23, 0, 0]);
        Assert.True(info.EndpointsGiven);
        Assert.Equal(2, info.HotSyncPort().InEndpoint);
        Assert.Equal(3, info.HotSyncPort().OutEndpoint);
    }
}
