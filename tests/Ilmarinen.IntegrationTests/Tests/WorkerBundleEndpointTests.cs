using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerBundleEndpointTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new IntegrationTestFixture();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private async Task<(string Path, string Hash)> SetupWithBundleAsync()
    {
        // The bundle file must exist before setup: the server hashes it at startup
        var bundlePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ilmarinen-test-bundle-{Guid.NewGuid():N}.zip");
        var content = Guid.NewGuid().ToByteArray();
        await System.IO.File.WriteAllBytesAsync(bundlePath, content);
        await _fixture.SetupAsync(workerBundlePath: bundlePath);
        _fixture.TrackBundleFile(bundlePath);
        return (bundlePath, Convert.ToHexString(SHA256.HashData(content)));
    }

    /// <summary>
    /// Guardian of the frozen manifest contract: asserts the exact wire-level field names and shapes the launcher parses. If this test breaks, every deployed launcher breaks with it.
    /// </summary>
    [Test]
    public async Task Manifest_OnWorkerPort_ReturnsSignedHashWithFrozenFieldNames()
    {
        var (_, expectedHash) = await SetupWithBundleAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.WorkerUrl) };
        var response = await client.GetAsync("/hub/workers/bundle/manifest");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.That(root.GetProperty("formatVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("bundleHash").GetString(), Is.EqualTo(expectedHash));
        Assert.That(root.GetProperty("targetFramework").GetString(), Is.EqualTo("net9.0"));

        // The signature covers "ilmarinen-worker-bundle:" + hash and must verify against the server public key — the same key the launcher extracts from ILMARINEN_WORKER_KEY.
        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
        var serverKey = _fixture.Services.GetRequiredService<ServerKeyService>();
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(serverKey.GetPublicKeyBase64()), out _);
        var signedData = Encoding.UTF8.GetBytes("ilmarinen-worker-bundle:" + expectedHash);
        Assert.That(publicKey.VerifyData(signedData, signature, HashAlgorithmName.SHA256), Is.True);
    }

    [Test]
    public async Task Download_OnWorkerPort_ReturnsExactBundleBytes()
    {
        var (bundlePath, _) = await SetupWithBundleAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.WorkerUrl) };
        var response = await client.GetAsync("/hub/workers/bundle/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.That(downloaded, Is.EqualTo(await System.IO.File.ReadAllBytesAsync(bundlePath)));
    }

    [Test]
    public async Task BundleEndpoints_OnPublicPort_ReturnNotFound()
    {
        await SetupWithBundleAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.ServerUrl) };

        var manifest = await client.GetAsync("/hub/workers/bundle/manifest");
        Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var download = await client.GetAsync("/hub/workers/bundle/download");
        Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task BundleEndpoints_WithoutConfiguredBundle_ReturnNotFound()
    {
        await _fixture.SetupAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.WorkerUrl) };

        var manifest = await client.GetAsync("/hub/workers/bundle/manifest");
        Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var download = await client.GetAsync("/hub/workers/bundle/download");
        Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
