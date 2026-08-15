using Ilmarinen.WorkerLauncher;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System;

var serverUrl = Environment.GetEnvironmentVariable("ILMARINEN_SERVER_URL")
    ?? throw new InvalidOperationException("ILMARINEN_SERVER_URL is not set.");
var workerKeyValue = Environment.GetEnvironmentVariable("ILMARINEN_WORKER_KEY")
    ?? throw new InvalidOperationException("ILMARINEN_WORKER_KEY is not set. Register this worker on the server first.");
var cacheRoot = Environment.GetEnvironmentVariable("ILMARINEN_BUNDLE_CACHE_PATH") ?? GetDefaultCachePath();

using var serverPublicKey = WorkerKey.GetServerPublicKey(workerKeyValue);
var cache = new BundleCache(cacheRoot);
cache.CleanStaleTmp(TimeSpan.FromHours(1));

using var http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };

// SIGTERM/SIGINT: stop the loop and shut the child down gracefully. Cancel = true keeps the runtime from tearing us down before the child is handled.
using var shutdownCts = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

Log($"Launcher starting. Server: {serverUrl}, bundle cache: {cacheRoot}");

string? lastRunHash = null;
string? previousDifferentHash = null;
var backoff = TimeSpan.Zero;

try
{
    while (!shutdownCts.IsCancellationRequested)
    {
        var runHash = await ResolveBundleAsync(shutdownCts.Token);
        if (runHash == null)
        {
            Log("No bundle available (manifest unreachable and nothing cached); retrying in 15s");
            await Task.Delay(TimeSpan.FromSeconds(15), shutdownCts.Token);
            continue;
        }

        if (RelaunchPolicy.ShouldSwitch(lastRunHash, runHash))
        {
            Log($"Switching bundle: {lastRunHash} -> {runHash}");
            previousDifferentHash = lastRunHash;
            backoff = TimeSpan.Zero;
        }
        else if (backoff > TimeSpan.Zero)
        {
            Log($"Relaunching same bundle after {backoff.TotalSeconds:F0}s backoff");
            await Task.Delay(backoff, shutdownCts.Token);
        }

        cache.Prune(runHash, previousDifferentHash);

        var started = DateTime.UtcNow;
        var exitCode = await RunWorkerAsync(runHash, shutdownCts.Token);
        if (shutdownCts.IsCancellationRequested)
        {
            break;
        }

        Log(exitCode == RelaunchPolicy.UpdateRequiredExitCode
            ? $"Worker requested an update (exit {exitCode})"
            : $"Worker exited with code {exitCode}");

        lastRunHash = runHash;
        backoff = DateTime.UtcNow - started >= RelaunchPolicy.StableUptime
            ? TimeSpan.Zero
            : RelaunchPolicy.NextBackoff(backoff);
    }
}
catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
{
    // Shutdown signal during a delay — normal exit
}

Log("Launcher stopped");
return 0;

// Returns the hash of a verified, extracted bundle to run — from the manifest when the server offers one, else the newest cached bundle — or null when neither exists.
async Task<string?> ResolveBundleAsync(CancellationToken ct)
{
    var manifest = await FetchManifestAsync(ct);
    if (manifest == null)
    {
        return cache.NewestCachedHash();
    }

    WarnOnRuntimeMismatch(manifest);

    if (!cache.IsCached(manifest.BundleHash) && !await DownloadAndExtractAsync(manifest, ct))
    {
        // Verification or download failure: never run unverified code; fall back to what we have.
        return cache.NewestCachedHash();
    }

    return manifest.BundleHash;
}

// Fetches the manifest, retrying transient connection failures a few times. Returns null on 404 (bundle serving disabled server-side), on an unusable manifest, or when the server stays unreachable — callers fall back to the cache.
async Task<BundleManifest?> FetchManifestAsync(CancellationToken ct)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        if (attempt > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        try
        {
            using var response = await http.GetAsync("hub/workers/bundle/manifest", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log("Server has no bundle configured (manifest 404)");
                return null;
            }
            response.EnsureSuccessStatusCode();
            return BundleManifest.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException ex)
        {
            // Contract break — a newer formatVersion or garbage. No retry will fix a launcher that is too old.
            Log($"ERROR: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Manifest fetch failed (attempt {attempt + 1}/3): {ex.Message}");
        }
    }
    return null;
}

async Task<bool> DownloadAndExtractAsync(BundleManifest manifest, CancellationToken ct)
{
    var tempZip = Path.Combine(cacheRoot, $"download-{Guid.NewGuid():N}.zip.tmp-partial");
    try
    {
        Log($"Downloading bundle {manifest.BundleHash}");
        using (var response = await http.GetAsync("hub/workers/bundle/download", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(tempZip);
            await response.Content.CopyToAsync(file, ct);
        }

        await using (var zipStream = File.OpenRead(tempZip))
        {
            if (!BundleVerifier.Verify(serverPublicKey, zipStream, manifest))
            {
                Log($"ERROR: bundle failed hash/signature verification; refusing to run it");
                return false;
            }
        }

        cache.Extract(tempZip, manifest.BundleHash);
        Log($"Bundle {manifest.BundleHash} verified and extracted");
        return true;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        Log($"Bundle download/extract failed: {ex.Message}");
        return false;
    }
    finally
    {
        if (File.Exists(tempZip))
        {
            File.Delete(tempZip);
        }
    }
}

async Task<int> RunWorkerAsync(string bundleHash, CancellationToken ct)
{
    var bundleDir = cache.BundleDirectory(bundleHash);
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = bundleDir,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add(Path.Combine(bundleDir, "ilmarinen-worker.dll"));
    // The manifest hash string verbatim, so every hash the server later compares originated from its own computation.
    startInfo.Environment["ILMARINEN_BUNDLE_HASH"] = bundleHash;

    Log($"Starting worker from bundle {bundleHash}");
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start worker process");

    try
    {
        await process.WaitForExitAsync(ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Forward the shutdown signal and give the worker time for an orderly stop; the deployment's stop_grace_period must exceed this window.
        Log("Forwarding SIGTERM to worker");
        sys_kill(process.Id, SigtermSignal);
        using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(graceCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("Worker did not stop in time; killing");
            process.Kill(entireProcessTree: true);
        }
    }

    return process.HasExited ? process.ExitCode : 0;
}

void WarnOnRuntimeMismatch(BundleManifest manifest)
{
    // "net9.0" -> 9. A bundle targeting a newer .NET major than this launcher's runtime cannot start; say so specifically instead of letting the child fail obscurely forever.
    var tfm = manifest.TargetFramework;
    if (tfm.StartsWith("net", StringComparison.Ordinal)
        && int.TryParse(tfm[3..].Split('.')[0], out var bundleMajor)
        && bundleMajor != Environment.Version.Major)
    {
        Log($"ERROR: bundle targets {tfm} but this launcher runs on .NET {Environment.Version.Major}. Update the launcher deployment on this host manually.");
    }
}

static string GetDefaultCachePath()
{
    var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrEmpty(dataDir))
    {
        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }
    return Path.Combine(dataDir, "ilmarinen", "bundles");
}

static void Log(string message)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [launcher] {message}");
}

partial class Program
{
    private const int SigtermSignal = 15;

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int sys_kill(int pid, int sig);
}
