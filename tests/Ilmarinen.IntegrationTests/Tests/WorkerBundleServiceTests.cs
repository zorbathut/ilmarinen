using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using NUnit.Framework;
using System.IO;
using System.Security.Cryptography;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class WorkerBundleServiceTests
{
    private static ServerKeyService CreateKeyService()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ServerKeyService(Convert.ToBase64String(key.ExportParameters(true).D!));
    }

    [Test]
    public void MissingBundleFile_ThrowsConfigurationException()
    {
        var config = new ServerConfig { WorkerBundlePath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.zip") };

        var ex = Assert.Throws<ConfigurationException>(() => new WorkerBundleService(config, CreateKeyService()));
        Assert.That(ex!.Message, Does.Contain("Server__WorkerBundlePath"));
    }

    [Test]
    public void UnsetBundlePath_DisablesServing()
    {
        var service = new WorkerBundleService(new ServerConfig(), CreateKeyService());

        Assert.That(service.IsEnabled, Is.False);
        Assert.That(service.CurrentHash, Is.Null);
    }
}
