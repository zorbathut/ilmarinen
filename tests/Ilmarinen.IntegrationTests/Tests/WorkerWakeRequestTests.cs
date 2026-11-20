using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Server.Services.Wake;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>An operator's wake address for a worker, and the Wake button that uses it.</summary>
[TestFixture]
[Category("Integration")]
public class WorkerWakeRequestTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private WorkerWaker Waker
    {
        get { return _fixture.Services.GetRequiredService<WorkerWaker>(); }
    }

    private RecordingWakeSender Sender
    {
        get { return _fixture.Services.GetRequiredService<RecordingWakeSender>(); }
    }

    private WorkerRepository Workers
    {
        get { return _fixture.Services.GetRequiredService<WorkerRepository>(); }
    }

    [Test]
    public async Task Wake_SendsToTheStoredAddress()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "wake-request");
        Assert.That(await Workers.SetWakeMacAddressAsync(worker.WorkerId, "aa:bb:cc:dd:ee:ff"), Is.True);

        Assert.That(await Waker.WakeNowAsync(worker.WorkerId, CancellationToken.None), Is.EqualTo(WakeRequestResult.Sent));

        Assert.That(Sender.Sent, Is.EqualTo(new[] { "aa:bb:cc:dd:ee:ff" }));
        Assert.That((await Workers.GetByIdAsync(worker.WorkerId))!.WakeMacAddress, Is.EqualTo("aa:bb:cc:dd:ee:ff"));
    }

    [Test]
    public async Task Wake_WithoutAnAddress_OrWithWakingOff_SendsNothing()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "wake-request-none");

        Assert.That(await Waker.WakeNowAsync(worker.WorkerId, CancellationToken.None), Is.EqualTo(WakeRequestResult.NoMacAddress));
        Assert.That(await Waker.WakeNowAsync(Guid.NewGuid(), CancellationToken.None), Is.EqualTo(WakeRequestResult.NoSuchWorker));

        await Workers.SetWakeMacAddressAsync(worker.WorkerId, "aa:bb:cc:dd:ee:ff");
        Sender.Enabled = false;
        Assert.That(await Waker.WakeNowAsync(worker.WorkerId, CancellationToken.None), Is.EqualTo(WakeRequestResult.WakingOff));

        Assert.That(Sender.Sent, Is.Empty);
    }

    [Test]
    public async Task ClearingTheAddress_LeavesNone()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "wake-request-clear");
        await Workers.SetWakeMacAddressAsync(worker.WorkerId, "aa:bb:cc:dd:ee:ff");

        await Workers.SetWakeMacAddressAsync(worker.WorkerId, null);

        Assert.That((await Workers.GetByIdAsync(worker.WorkerId))!.WakeMacAddress, Is.Null);
        Assert.That(await Workers.SetWakeMacAddressAsync(Guid.NewGuid(), null), Is.False);
    }
}
