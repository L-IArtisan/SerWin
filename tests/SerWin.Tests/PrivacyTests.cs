using SerWin.Core.Privacy;

namespace SerWin.Tests;

public class CapabilityUsageTests
{
    private static long Ft(DateTime d) => d.ToFileTime();

    [Fact]
    public void Desktop_app_key_becomes_a_path()
    {
        var use = CapabilityUsage.Create("microphone", @"C:#Program Files (x86)#Steam#steamapps#common#Big Walk#Big Walk.exe", packaged: false,
            permission: null, Ft(new DateTime(2026, 10, 24, 23, 25, 47)), Ft(new DateTime(2026, 10, 24, 23, 25, 50)))!;
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Big Walk\Big Walk.exe", use.ExecutablePath);
        Assert.Equal("Big Walk.exe", use.DisplayName);
        Assert.False(use.InUse);
        Assert.Equal(TimeSpan.FromSeconds(3), use.Duration);
    }

    [Fact]
    public void Store_app_shows_its_family_name()
    {
        var use = CapabilityUsage.Create("microphone", "Claude_pzs8sxrjxfjjc", packaged: true, "Allow", Ft(DateTime.Now.AddDays(-1)), Ft(DateTime.Now.AddDays(-1).AddSeconds(10)))!;
        Assert.Equal("Claude", use.DisplayName);
        Assert.Null(use.ExecutablePath);
        Assert.Equal("WindowsCamera", CapabilityUsage.Create("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", true, "Allow", Ft(DateTime.Now), null)!.DisplayName);
    }

    [Fact]
    public void Start_after_stop_means_in_use_right_now()
    {
        var now = DateTime.Now;
        Assert.True(CapabilityUsage.Create("webcam", "x_y", true, "Allow", Ft(now), Ft(now.AddMinutes(-5)))!.InUse);
        Assert.True(CapabilityUsage.Create("webcam", "x_y", true, "Allow", Ft(now), null)!.InUse);
        Assert.False(CapabilityUsage.Create("webcam", "x_y", true, "Allow", Ft(now.AddMinutes(-5)), Ft(now))!.InUse);
    }

    [Fact]
    public void Never_used_is_ignored() => Assert.Null(CapabilityUsage.Create("location", "x_y", true, "Deny", null, null));
}
