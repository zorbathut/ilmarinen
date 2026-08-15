using Ilmarinen.Server.Services;
using Ilmarinen.WorkerLauncher;
using NUnit.Framework;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Unit tests for the launcher's decision logic. Tests referencing the launcher project is fine — the frozen-contract rule only forbids the launcher referencing Ilmarinen projects. BundleVerifier is deliberately round-tripped against a real ServerKeyService signature so the two sides of the signing contract are tested against each other.
/// </summary>
[TestFixture]
public class WorkerLauncherTests
{
    // --- BundleManifest.Parse ---

    [Test]
    public void Parse_ValidManifest_ReturnsFields()
    {
        var manifest = BundleManifest.Parse(
            """{"formatVersion":1,"bundleHash":"ABCD","signature":"c2ln","targetFramework":"net9.0"}""");

        Assert.That(manifest.BundleHash, Is.EqualTo("ABCD"));
        Assert.That(manifest.Signature, Is.EqualTo("c2ln"));
        Assert.That(manifest.TargetFramework, Is.EqualTo("net9.0"));
    }

    [Test]
    public void Parse_ExtraFields_AreTolerated()
    {
        var manifest = BundleManifest.Parse(
            """{"formatVersion":1,"bundleHash":"ABCD","signature":"c2ln","targetFramework":"net9.0","futureField":true}""");

        Assert.That(manifest.BundleHash, Is.EqualTo("ABCD"));
    }

    [Test]
    public void Parse_UnsupportedFormatVersion_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => BundleManifest.Parse(
            """{"formatVersion":2,"bundleHash":"ABCD","signature":"c2ln","targetFramework":"net9.0"}"""));
        Assert.That(ex!.Message, Does.Contain("too old"));
    }

    [Test]
    public void Parse_MissingFormatVersion_Throws()
    {
        Assert.Throws<FormatException>(() => BundleManifest.Parse(
            """{"bundleHash":"ABCD","signature":"c2ln","targetFramework":"net9.0"}"""));
    }

    [Test]
    public void Parse_MissingField_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => BundleManifest.Parse(
            """{"formatVersion":1,"bundleHash":"ABCD","targetFramework":"net9.0"}"""));
        Assert.That(ex!.Message, Does.Contain("signature"));
    }

    // --- BundleVerifier ---

    private static (ServerKeyService Server, ECDsa PublicKey) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new ServerKeyService(Convert.ToBase64String(key.ExportParameters(true).D!));
        var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(server.GetPublicKeyBase64()), out _);
        return (server, publicKey);
    }

    private static BundleManifest SignedManifest(ServerKeyService server, byte[] bundleContent)
    {
        // Mirrors WorkerBundleService's signing scheme
        var hash = Convert.ToHexString(SHA256.HashData(bundleContent));
        var signature = server.Sign(Encoding.UTF8.GetBytes(BundleVerifier.SignaturePrefix + hash));
        return new BundleManifest
        {
            BundleHash = hash,
            Signature = Convert.ToBase64String(signature),
            TargetFramework = "net9.0"
        };
    }

    [Test]
    public void Verify_ServerSignedBundle_Passes()
    {
        var (server, publicKey) = CreateKeyPair();
        using (publicKey)
        {
            var content = Guid.NewGuid().ToByteArray();
            var manifest = SignedManifest(server, content);

            using var stream = new MemoryStream(content);
            Assert.That(BundleVerifier.Verify(publicKey, stream, manifest), Is.True);
        }
    }

    [Test]
    public void Verify_TamperedContent_Fails()
    {
        var (server, publicKey) = CreateKeyPair();
        using (publicKey)
        {
            var content = Guid.NewGuid().ToByteArray();
            var manifest = SignedManifest(server, content);

            content[0] ^= 0xFF;
            using var stream = new MemoryStream(content);
            Assert.That(BundleVerifier.Verify(publicKey, stream, manifest), Is.False);
        }
    }

    [Test]
    public void Verify_WrongServerKey_Fails()
    {
        var (server, _) = CreateKeyPair();
        var (_, otherPublicKey) = CreateKeyPair();
        using (otherPublicKey)
        {
            var content = Guid.NewGuid().ToByteArray();
            var manifest = SignedManifest(server, content);

            using var stream = new MemoryStream(content);
            Assert.That(BundleVerifier.Verify(otherPublicKey, stream, manifest), Is.False);
        }
    }

    [Test]
    public void Verify_HashMismatch_Fails()
    {
        var (server, publicKey) = CreateKeyPair();
        using (publicKey)
        {
            var content = Guid.NewGuid().ToByteArray();
            // A validly-signed manifest for different content: hash comparison must fail before the signature can mislead
            var manifest = SignedManifest(server, Guid.NewGuid().ToByteArray());

            using var stream = new MemoryStream(content);
            Assert.That(BundleVerifier.Verify(publicKey, stream, manifest), Is.False);
        }
    }

    [Test]
    public void Verify_MalformedSignatureBase64_Fails()
    {
        var (server, publicKey) = CreateKeyPair();
        using (publicKey)
        {
            var content = Guid.NewGuid().ToByteArray();
            var manifest = SignedManifest(server, content) with { Signature = "not-base64!" };

            using var stream = new MemoryStream(content);
            Assert.That(BundleVerifier.Verify(publicKey, stream, manifest), Is.False);
        }
    }

    // --- RelaunchPolicy ---

    [Test]
    public void ShouldSwitch_ChangedHash_True()
    {
        Assert.That(RelaunchPolicy.ShouldSwitch("AAAA", "BBBB"), Is.True);
    }

    [Test]
    public void ShouldSwitch_SameHash_False()
    {
        Assert.That(RelaunchPolicy.ShouldSwitch("AAAA", "AAAA"), Is.False);
    }

    [Test]
    public void ShouldSwitch_FirstLaunch_False()
    {
        Assert.That(RelaunchPolicy.ShouldSwitch(null, "AAAA"), Is.False);
    }

    [Test]
    public void NextBackoff_GrowsFromZeroAndCaps()
    {
        var backoff = RelaunchPolicy.NextBackoff(TimeSpan.Zero);
        Assert.That(backoff, Is.EqualTo(RelaunchPolicy.InitialBackoff));

        for (var i = 0; i < 10; i++)
        {
            backoff = RelaunchPolicy.NextBackoff(backoff);
        }
        Assert.That(backoff, Is.EqualTo(RelaunchPolicy.MaxBackoff));
    }

    [Test]
    public void UpdateRequiredExitCode_MatchesWorkerConstant()
    {
        // The launcher deliberately duplicates the constant; this pins the two together.
        Assert.That(RelaunchPolicy.UpdateRequiredExitCode, Is.EqualTo(Ilmarinen.Worker.WorkerExitCodes.UpdateRequired));
    }

    // --- BundleCache filesystem behavior ---

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateTestZip(string root, string innerFileName, string content)
    {
        var srcDir = Path.Combine(root, "zip-src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, innerFileName), content);
        var zipPath = Path.Combine(root, "test.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(srcDir, zipPath);
        Directory.Delete(srcDir, recursive: true);
        return zipPath;
    }

    [Test]
    public void Extract_MakesBundleCachedWithContent()
    {
        var root = CreateTempRoot();
        try
        {
            var zipPath = CreateTestZip(root, "marker.txt", "hello");
            var cache = new BundleCache(root);

            cache.Extract(zipPath, "HASH1");

            Assert.That(cache.IsCached("HASH1"), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(cache.BundleDirectory("HASH1"), "marker.txt")), Is.EqualTo("hello"));
            Assert.That(Directory.GetDirectories(root, "*.tmp-*"), Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Extract_TargetAlreadyExists_KeepsWinnerAndCleansTmp()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new BundleCache(root);
            // Another launcher already extracted this hash
            Directory.CreateDirectory(cache.BundleDirectory("HASH1"));
            File.WriteAllText(Path.Combine(cache.BundleDirectory("HASH1"), "winner.txt"), "first");

            var zipPath = CreateTestZip(root, "loser.txt", "second");
            cache.Extract(zipPath, "HASH1");

            Assert.That(File.Exists(Path.Combine(cache.BundleDirectory("HASH1"), "winner.txt")), Is.True);
            Assert.That(Directory.GetDirectories(root, "*.tmp-*"), Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CleanStaleTmp_RemovesOldLeftoversKeepsFreshOnes()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new BundleCache(root);
            Directory.CreateDirectory(Path.Combine(root, "HASH1.tmp-abc"));
            File.WriteAllText(Path.Combine(root, "download-abc.zip.tmp-partial"), "partial");

            // A fresh extraction/download must survive an age-gated sweep
            cache.CleanStaleTmp(TimeSpan.FromHours(1));
            Assert.That(Directory.Exists(Path.Combine(root, "HASH1.tmp-abc")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "download-abc.zip.tmp-partial")), Is.True);

            // A negative age puts the cutoff in the future, standing in for leftovers older than the gate (creation times can't be back-dated on Linux)
            cache.CleanStaleTmp(TimeSpan.FromMinutes(-1));
            Assert.That(Directory.Exists(Path.Combine(root, "HASH1.tmp-abc")), Is.False);
            Assert.That(File.Exists(Path.Combine(root, "download-abc.zip.tmp-partial")), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task NewestCachedHash_PicksMostRecentlyCreated()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new BundleCache(root);
            Assert.That(cache.NewestCachedHash(), Is.Null);

            Directory.CreateDirectory(cache.BundleDirectory("OLDER"));
            await Task.Delay(50);
            Directory.CreateDirectory(cache.BundleDirectory("NEWER"));

            Assert.That(cache.NewestCachedHash(), Is.EqualTo("NEWER"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- BundleCache prune selection ---

    [Test]
    public void SelectPrunable_KeepsCurrentAndPrevious()
    {
        var prunable = BundleCache.SelectPrunable(["AAAA", "BBBB", "CCCC", "DDDD"], currentHash: "AAAA", previousHash: "BBBB");
        Assert.That(prunable, Is.EquivalentTo(new[] { "CCCC", "DDDD" }));
    }

    [Test]
    public void SelectPrunable_NoPrevious_KeepsOnlyCurrent()
    {
        var prunable = BundleCache.SelectPrunable(["AAAA", "BBBB"], currentHash: "AAAA", previousHash: null);
        Assert.That(prunable, Is.EquivalentTo(new[] { "BBBB" }));
    }
}
