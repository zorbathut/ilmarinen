using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using NUnit.Framework;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class ServerKeyServiceTests
{
    [Test]
    public void ParseKeyBytes_ValidKey_Returns32Bytes()
    {
        var key = Convert.ToBase64String(new byte[32]);

        var result = ServerKeyService.ParseKeyBytes(key);

        Assert.That(result, Has.Length.EqualTo(32));
    }

    // A typo'd key is an operator mistake, not a bug: it must surface as ConfigurationException (503),
    // never as the InvalidOperationException that would become a 500.
    [Test]
    public void ParseKeyBytes_NotBase64_ThrowsConfigurationException()
    {
        var ex = Assert.Throws<ConfigurationException>(() => ServerKeyService.ParseKeyBytes("not-valid-base64!!"));

        Assert.That(ex!.Message, Does.Contain("base64"));
    }

    [TestCase(16)]
    [TestCase(31)]
    [TestCase(64)]
    public void ParseKeyBytes_WrongLength_ThrowsConfigurationException(int byteCount)
    {
        var key = Convert.ToBase64String(new byte[byteCount]);

        var ex = Assert.Throws<ConfigurationException>(() => ServerKeyService.ParseKeyBytes(key));

        Assert.That(ex!.Message, Does.Contain("32-byte"));
    }
}
