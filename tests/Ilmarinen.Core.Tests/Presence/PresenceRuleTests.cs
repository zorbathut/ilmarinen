using Ilmarinen.Worker.Host;
using Ilmarinen.Worker.Presence;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests.Presence;

/// <summary>Which devices say a person is there, which events count, and how input, other programs' inhibitors and the screen lock add up to "in use".</summary>
[TestFixture]
public class PresenceRuleTests
{
    // Captured from a KDE desktop: the power button and the ACPI video bus (both on the host bus) and the PC speaker get a kbd handler; a headphone jack sensor gets none.
    private const string Devices = """
        I: Bus=0019 Vendor=0000 Product=0001 Version=0000
        N: Name="Power Button"
        P: Phys=PNP0C0C/button/input0
        S: Sysfs=/devices/platform/PNP0C0C:00/input/input0
        U: Uniq=
        H: Handlers=kbd event0 
        B: PROP=0
        B: EV=3
        B: KEY=8000 10000000000000 0

        I: Bus=0019 Vendor=0000 Product=0006 Version=0000
        N: Name="Video Bus"
        P: Phys=LNXVIDEO/video/input0
        S: Sysfs=/devices/pci0000:00/0000:00:08.1/acpi.video_bus.0/input/input2
        U: Uniq=
        H: Handlers=kbd event2 
        B: PROP=0
        B: EV=3
        B: KEY=3e000b00000000 0 0 0

        I: Bus=0003 Vendor=24f0 Product=0140 Version=0110
        N: Name="Metadot - Das Keyboard Das Keyboard"
        P: Phys=usb-0000:0e:00.4-2.4/input0
        S: Sysfs=/devices/pci0000:00/0000:00:08.1/0000:0e:00.4/usb5/5-2/5-2.4/5-2.4:1.0/0003:24F0:0140.0002/input/input3
        U: Uniq=
        H: Handlers=sysrq kbd leds event3 
        B: PROP=0
        B: EV=120013
        B: KEY=e080ffdf01cfffff fffffffffffffffe
        B: MSC=10
        B: LED=1f

        I: Bus=0003 Vendor=24f0 Product=0140 Version=0110
        N: Name="Metadot - Das Keyboard Das Keyboard Consumer Control"
        P: Phys=usb-0000:0e:00.4-2.4/input1
        S: Sysfs=/devices/pci0000:00/0000:00:08.1/0000:0e:00.4/usb5/5-2/5-2.4/5-2.4:1.1/0003:24F0:0140.0003/input/input5
        U: Uniq=
        H: Handlers=kbd event5 
        B: PROP=0
        B: EV=13
        B: KEY=1000002020000 3878d8011001 e000000000000 0
        B: MSC=10

        I: Bus=0010 Vendor=001f Product=0001 Version=0100
        N: Name="PC Speaker"
        P: Phys=isa0061/input0
        S: Sysfs=/devices/platform/pcspkr/input/input12
        U: Uniq=
        H: Handlers=kbd event12 
        B: PROP=0
        B: EV=40001
        B: SND=6

        I: Bus=0000 Vendor=0000 Product=0000 Version=0000
        N: Name="HD-Audio Generic Front Headphone"
        P: Phys=ALSA
        S: Sysfs=/devices/pci0000:00/0000:00:08.1/0000:0e:00.6/sound/card2/input29
        U: Uniq=
        H: Handlers=event25 
        B: PROP=0
        B: EV=21
        B: SW=4

        I: Bus=0003 Vendor=1532 Product=0090 Version=0111
        N: Name="Razer Razer Naga Pro"
        P: Phys=usb-0000:0f:00.0-1.2/input1
        S: Sysfs=/devices/pci0000:00/0000:00:08.3/0000:0f:00.0/usb7/7-1/7-1.2/7-1.2:1.1/0003:1532:0090.0005/input/input30
        U: Uniq=000000000000
        H: Handlers=sysrq kbd leds event26 mouse1 
        B: PROP=0
        B: EV=12001f
        B: KEY=733eff 0 0 483ffff17aff32d bfd4444600000000 3f0001 130c730b17c007 ffbf7bfad941dfff febeffdfffefffff fffffffffffffffe
        B: REL=1943
        B: ABS=100000000
        B: MSC=10
        B: LED=7
        """;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private static LogindInhibitor Inhibitor(string what, string who, string mode)
    {
        return new LogindInhibitor(what, who, "because", mode, 1000, 42);
    }

    private static LogindSession Session(string seat = "seat0", string cls = "user", string type = "wayland", bool active = true, bool locked = false)
    {
        return new LogindSession("2", seat, cls, type, active, locked);
    }

    [Test]
    public void Parse_KeepsKeyboardAndMouseHandlers_AndNotJackSensorsOrHostBusButtons()
    {
        var devices = InputDevices.Parse(Devices);

        Assert.That(devices.Select(d => d.EventNode), Is.EqualTo(new[] { "event3", "event5", "event12", "event26" }));
        Assert.That(devices.Single(d => d.EventNode == "event3").Name, Is.EqualTo("Metadot - Das Keyboard Das Keyboard"));
    }

    [Test]
    public void Parse_KeepsAMouseWithNoKeyboardHandler()
    {
        var devices = InputDevices.Parse("""
            N: Name="A mouse"
            H: Handlers=event6 mouse0

            N: Name="Not a mouse"
            H: Handlers=event7 mousedev
            """);

        Assert.That(devices.Select(d => d.EventNode), Is.EqualTo(new[] { "event6" }));
    }

    [TestCase((ushort)0x00, false, TestName = "EV_SYN")]
    [TestCase((ushort)0x01, true, TestName = "EV_KEY")]
    [TestCase((ushort)0x02, true, TestName = "EV_REL")]
    [TestCase((ushort)0x03, true, TestName = "EV_ABS")]
    [TestCase((ushort)0x04, false, TestName = "EV_MSC")]
    [TestCase((ushort)0x05, false, TestName = "EV_SW")]
    [TestCase((ushort)0x11, false, TestName = "EV_LED")]
    [TestCase((ushort)0x14, false, TestName = "EV_REP")]
    public void IsHumanInput_CountsKeysMovementAndTouch(ushort type, bool human)
    {
        var bytes = new byte[InputEvent.Size];
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), type);

        Assert.That(InputEvent.IsHumanInput(bytes), Is.EqualTo(human));
    }

    [Test]
    public void RecentInput_IsInUse_UntilTheIdleTimePasses()
    {
        var justInside = PresenceRule.Judge(Now - Idle + TimeSpan.FromSeconds(1), "kbd", Idle, Now, locked: false, []);
        var justOutside = PresenceRule.Judge(Now - Idle, "kbd", Idle, Now, locked: false, []);
        var never = PresenceRule.Judge(null, null, Idle, Now, locked: false, []);

        Assert.That(justInside.InUse, Is.True);
        Assert.That(justOutside.InUse, Is.False);
        Assert.That(never.InUse, Is.False);
        Assert.That(justInside.Reason, Is.Not.EqualTo(justOutside.Reason));
    }

    [Test]
    public void AnotherProgramKeepingTheMachineAwake_IsInUse_WithNoInput()
    {
        var verdict = PresenceRule.Judge(null, null, Idle, Now, locked: false, [Inhibitor("idle", "firefox", "block")]);

        Assert.That(verdict.InUse, Is.True);
        Assert.That(verdict.Reason, Does.Contain("firefox"));
    }

    [Test]
    public void ALockedScreen_IsNotInUse_WhateverElse()
    {
        var verdict = PresenceRule.Judge(Now, "kbd", Idle, Now, locked: true, [Inhibitor("idle", "firefox", "block")]);

        Assert.That(verdict.InUse, Is.False);
    }

    [TestCase("idle", "firefox", "block", true)]
    [TestCase("sleep", "mpv", "block", true)]
    [TestCase("idle:sleep", "vlc", "block", true)]
    [TestCase("shutdown:sleep", "packagekit", "block", true)]
    [TestCase("sleep", "NetworkManager", "delay", false)]
    [TestCase("handle-power-key:handle-suspend-key:handle-hibernate-key:handle-lid-switch", "PowerDevil", "block", false)]
    [TestCase("shutdown", "packagekit", "block", false)]
    [TestCase("idle:sleep", PresenceRule.OwnInhibitorWho, "block", false)]
    public void KeepsTheMachineAwake_OnlyOtherProgramsBlockingIdleOrSleep(string what, string who, string mode, bool counts)
    {
        Assert.That(PresenceRule.KeepsTheMachineAwake(Inhibitor(what, who, mode)), Is.EqualTo(counts));
    }

    [Test]
    public void IsLocked_FollowsTheActiveGraphicalSessionAtTheSeat()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PresenceRule.IsLocked([Session()]), Is.False);
            Assert.That(PresenceRule.IsLocked([Session(locked: true)]), Is.True);
            Assert.That(PresenceRule.IsLocked([Session(type: "x11")]), Is.False);
            Assert.That(PresenceRule.IsLocked([Session(type: "tty")]), Is.True, "a console login isn't someone at the desktop");
            Assert.That(PresenceRule.IsLocked([Session(cls: "greeter")]), Is.True, "the login screen");
            Assert.That(PresenceRule.IsLocked([Session(active: false, locked: false), Session(cls: "manager", type: "unspecified", seat: "")]), Is.True, "switched away");
            Assert.That(PresenceRule.IsLocked([Session(seat: "", type: "wayland")]), Is.True, "a remote session has no seat");
            Assert.That(PresenceRule.IsLocked([]), Is.True, "nobody logged in");
        });
    }
}
