// Matrix Build Pipeline
//
// Build across multiple platforms and configurations in parallel.
// Shows how to generate jobs dynamically and fan-in for aggregation.

#r "Conductor"

// Define the matrix
var platforms = new[] { "linux-x64", "win-x64", "osx-x64", "osx-arm64" };
var configurations = new[] { "Debug", "Release" };

// Generate build jobs for each combination
var buildJobs = new List<string>();

foreach (var platform in platforms)
{
    foreach (var config in configurations)
    {
        var jobName = $"build-{platform}-{config.ToLower()}";
        buildJobs.Add(jobName);
        
        Step(jobName)
            .Image("mcr.microsoft.com/dotnet/sdk:8.0")
            .DisplayName($"Build {platform} ({config})")
            .Run(async ctx =>
            {
                await ctx.Exec("dotnet", "publish",
                    "-c", config,
                    "-r", platform,
                    "-o", $"out/{platform}/{config}");
                
                ctx.Output("binaries", $"out/{platform}/{config}/**/*");
            });
    }
}

// Create release archives for Release builds only
var releaseJobs = platforms.Select(p => $"build-{p}-release").ToArray();

foreach (var platform in platforms)
{
    Step($"package-{platform}")
        .Image("alpine:latest")
        .Needs($"build-{platform}-release")
        .Run(async ctx =>
        {
            ctx.Input("binaries");
            await ctx.Exec("apk", "add", "--no-cache", "zip");
            await ctx.Exec("zip", "-r", $"release-{platform}.zip", $"out/{platform}/Release/");
            ctx.Output("release", $"release-{platform}.zip", fingerprint: true);
        });
}

// Aggregate all release packages
Step("create-release")
    .Image("alpine:latest")
    .Needs(platforms.Select(p => $"package-{p}").ToArray())
    .When(ctx => ctx.Branch == "main" || ctx.Branch.StartsWith("release/"))
    .Run(async ctx =>
    {
        // Download all platform releases
        foreach (var platform in platforms)
        {
            ctx.Input("release", from: $"package-{platform}");
        }
        
        await ctx.Exec("ls", "-la");
        
        // Could upload to GitHub releases, S3, etc.
        ctx.Output("all-releases", "release-*.zip");
    });

// Run tests for each platform (can run in parallel with packaging)
foreach (var platform in platforms)
{
    Step($"test-{platform}")
        .Image("mcr.microsoft.com/dotnet/sdk:8.0")
        .Needs($"build-{platform}-release")
        .Run(async ctx =>
        {
            await ctx.Exec("dotnet", "test",
                "-c", "Release",
                "-r", platform,
                "--no-build");
        });
}

// Final gate before release
Step("release-gate")
    .Image("alpine:latest")
    .Needs(
        platforms.Select(p => $"test-{p}")
            .Concat(platforms.Select(p => $"package-{p}"))
            .ToArray()
    )
    .Run(ctx => ctx.Exec("echo", "All platforms built and tested successfully"));
