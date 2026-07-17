using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class CustomerDisplayMonitorPolicyTests
{
    [TestMethod]
    public void OneMonitor_HasNoCustomerFallback()
    {
        var result = Select(M("P", true, 0, 0, 1024, 768));
        Assert.IsNull(result.Customer);
        Assert.AreEqual("customer_monitor_unavailable", result.ErrorCode);
    }

    [TestMethod]
    public void TwoMonitors_SelectsNonCashier()
    {
        var result = Select(M("P", true, 0, 0, 1024, 768), M("C", false, 1024, 0, 1366, 768));
        Assert.AreEqual("P", result.Cashier.DeviceName);
        Assert.AreEqual("C", result.Customer.DeviceName);
    }

    [TestMethod]
    public void ThreeMonitors_PrefersLastValidChoice()
    {
        var settings = CustomerDisplaySettings.CreateDefault(3);
        settings.CustomerMonitorDeviceName = "C2";
        var result = CustomerDisplayMonitorPolicy.Select(new[]
        {
            M("P", true, 0, 0, 1024, 768), M("C1", false, 1024, 0, 1024, 768), M("C2", false, -900, 0, 900, 1440)
        }, settings);
        Assert.AreEqual("C2", result.Customer.DeviceName);
    }

    [TestMethod]
    public void NegativeAndPortraitBounds_ArePreserved()
    {
        var result = Select(M("P", true, 0, 0, 1366, 768), M("LEFT", false, -1080, -200, 1080, 1920));
        Assert.AreEqual(-1080, result.Customer.Left);
        Assert.AreEqual(-200, result.Customer.Top);
        Assert.IsTrue(result.Customer.Height > result.Customer.Width);
    }

    [TestMethod]
    public void PrimaryOnRight_StillSelectsPrimaryAsCashier()
    {
        var result = Select(M("L", false, -1024, 0, 1024, 768), M("P", true, 0, 0, 1366, 768));
        Assert.AreEqual("P", result.Cashier.DeviceName);
        Assert.AreEqual("L", result.Customer.DeviceName);
    }

    [TestMethod]
    public void DuplicateBounds_AreRejected()
    {
        var result = Select(M("A", true, 0, 0, 1920, 1080), M("B", false, 0, 0, 1920, 1080));
        Assert.AreEqual(CustomerDisplayTopologyMode.Duplicate, result.TopologyMode);
        Assert.IsNull(result.Customer);
        Assert.AreEqual("extend_required", result.ErrorCode);
    }

    [TestMethod]
    public void ManualMissingSelection_DoesNotFallback()
    {
        var settings = CustomerDisplaySettings.CreateDefault(2);
        settings.SelectionMode = CustomerDisplaySelectionMode.Manual;
        settings.CustomerMonitorDeviceName = "MISSING";
        var result = CustomerDisplayMonitorPolicy.Select(new[] { M("P", true, 0, 0, 1000, 700), M("C", false, 1000, 0, 1000, 700) }, settings);
        Assert.IsNull(result.Customer);
        Assert.AreEqual("selected_monitor_missing", result.ErrorCode);
    }

    [TestMethod]
    public void SameCashierAndCustomer_IsRejected()
    {
        var settings = CustomerDisplaySettings.CreateDefault(2);
        settings.SelectionMode = CustomerDisplaySelectionMode.Manual;
        settings.CashierMonitorDeviceName = "P";
        settings.CustomerMonitorDeviceName = "P";
        var result = CustomerDisplayMonitorPolicy.Select(new[] { M("P", true, 0, 0, 1000, 700), M("C", false, 1000, 0, 1000, 700) }, settings);
        Assert.IsNull(result.Customer);
        Assert.AreEqual("same_monitor", result.ErrorCode);
    }

    [TestMethod]
    public void Reconnect_RestoresSavedAutomaticMonitor()
    {
        var settings = CustomerDisplaySettings.CreateDefault(2);
        settings.CustomerMonitorDeviceName = "C";
        var disconnected = CustomerDisplayMonitorPolicy.Select(new[] { M("P", true, 0, 0, 1000, 700) }, settings);
        var reconnected = CustomerDisplayMonitorPolicy.Select(new[] { M("P", true, 0, 0, 1000, 700), M("C", false, -800, 0, 800, 600) }, settings);
        Assert.IsNull(disconnected.Customer);
        Assert.AreEqual("C", reconnected.Customer.DeviceName);
    }

    private static CustomerDisplayMonitorSelection Select(params CustomerDisplayMonitorDescriptor[] monitors) =>
        CustomerDisplayMonitorPolicy.Select(monitors, CustomerDisplaySettings.CreateDefault(monitors.Length));

    private static CustomerDisplayMonitorDescriptor M(string name, bool primary, int left, int top, int width, int height) =>
        new() { DeviceName = name, IsPrimary = primary, Left = left, Top = top, Width = width, Height = height };
}
