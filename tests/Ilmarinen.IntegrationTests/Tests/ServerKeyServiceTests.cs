using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using NUnit.Framework;
using System.Linq;
using System.Security.Cryptography;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class ServerKeyServiceTests
{
    private static string ValidKeyBase64()
    {
        return Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportParameters(true).D!);
    }

    [Test]
    public void ValidKey_IsEnabled()
    {
        var service = new ServerKeyService(ValidKeyBase64());

        Assert.That(service.IsEnabled, Is.True);
        Assert.That(service.GetEncryptionKey(), Has.Length.EqualTo(32));
    }

    [Test]
    public void MissingKey_StaysDisabledRatherThanThrowing()
    {
        var service = new ServerKeyService(null);

        Assert.That(service.IsEnabled, Is.False);
    }

    [Test]
    public void PlaceholderKey_StaysDisabledRatherThanThrowing()
    {
        var service = new ServerKeyService("REPLACE-ME-WITH-REAL-KEY-GENERATED-VIA-openssl-rand-base64-32");

        Assert.That(service.IsEnabled, Is.False);
    }

    // Every malformed key below is an operator mistake, so each must raise ConfigurationException (503),
    // never an exception that would escape as a 500 or as a raw OpenSSL error.
    [Test]
    public void NotBase64_ThrowsConfigurationException()
    {
        var ex = Assert.Throws<ConfigurationException>(() => new ServerKeyService("REPLACE_WITH_GENERATED_KEY"));

        Assert.That(ex!.Message, Does.Contain("base64"));
    }

    [TestCase(16)]
    [TestCase(31)]
    [TestCase(64)]
    public void WrongLength_ThrowsConfigurationException(int byteCount)
    {
        var ex = Assert.Throws<ConfigurationException>(() => new ServerKeyService(Convert.ToBase64String(new byte[byteCount])));

        Assert.That(ex!.Message, Does.Contain("32-byte"));
    }

    // Right shape, unusable scalar: zero is the point at infinity, and all-0xFF exceeds the curve order.
    // ECDsa rejects both with CryptographicException, which must not reach the operator raw.
    [Test]
    public void ZeroScalar_ThrowsConfigurationException()
    {
        var ex = Assert.Throws<ConfigurationException>(() => new ServerKeyService(Convert.ToBase64String(new byte[32])));

        Assert.That(ex!.Message, Does.Contain("not a valid P-256 private key"));
    }

    [Test]
    public void ScalarAboveCurveOrder_ThrowsConfigurationException()
    {
        var key = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 32).ToArray());

        var ex = Assert.Throws<ConfigurationException>(() => new ServerKeyService(key));

        Assert.That(ex!.Message, Does.Contain("not a valid P-256 private key"));
    }
}
