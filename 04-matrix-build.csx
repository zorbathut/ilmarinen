// Matrix Build Pipeline
//
// Build across multiple platforms and configurations in parallel.
// Shows how to generate jobs dynamically and fan-in for aggregation.

#r "Conductor"

// Define the matrix
var platforms = new[] { "linux-x64", "win-x64", "osx-x64", "osx-arm64" };
var configurations = new[] { "Debug", "Release" };

// Generate build jobs for each combination - store references in a dictionary
var builds = new Dictionary<(string platform, string config), Step>();

foreach (var platform in platforms)
{
    foreach (var config in configurations)
    {
        var jobName = $"build-{platform}-{config.ToLower()}";

        builds[(platform, config)] = Step(jobName)
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

// Create release archives for Release builds only - store package step references
var packages = new Dictionary<string, Step>();

foreach (var platform in platforms)
{
    packages[platform] = Step($"package-{platform}")
        .Image("alpine:latest")
        .Needs(builds[(platform, "Release")])
        .Run(async ctx =>
        {
            ctx.Input("binaries");
            await ctx.Exec("apk", "add", "--no-cache", "zip");
            await ctx.Exec("zip", "-r", $"release-{platform}.zip", $"out/{platform}/Release/");
            ctx.Output("release", $"release-{platform}.zip", fingerprint: true);
        });
}

// Aggregate all release packages
var createRelease = Step("create-release")
    .Image("alpine:latest")
    .Needs(packages.Values.ToArray())
    .When(ctx => ctx.Branch == "main" || ctx.Branch.StartsWith("release/"))
    .Run(async ctx =>
    {
        // Download all platform releases (still need string for wildcard pattern)
        ctx.Input("release", from: "package-*");

        await ctx.Exec("ls", "-la");

        // Could upload to GitHub releases, S3, etc.
        ctx.Output("all-releases", "release-*.zip");
    });

// Run tests for each platform (can run in parallel with packaging)
var tests = new Dictionary<string, Step>();

foreach (var platform in platforms)
{
    tests[platform] = Step($"test-{platform}")
        .Image("mcr.microsoft.com/dotnet/sdk:8.0")
        .Needs(builds[(platform, "Release")])
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
    .Needs(tests.Values.Concat(packages.Values).ToArray())
    .Run(ctx => ctx.Exec("echo", "All platforms built and tested successfully"));
