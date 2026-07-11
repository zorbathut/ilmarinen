// Demonstrates persistent workspace feature
// In worker mode, the workspace persists between job runs, enabling:
// - Faster incremental builds (cached build artifacts)
// - Preserved package caches (NuGet, npm, etc.)
//
// In CLI mode, Workspace() is informational only (uses current directory)

Workspace("demo-cache");

Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        // First run: restore downloads packages; subsequent runs reuse the package cache in the persistent workspace
        await ctx.Shell(@"
            echo 'Checking for existing packages...'
            if [ -d ~/.nuget ]; then
                echo 'NuGet cache exists - will use cached packages'
            else
                echo 'No NuGet cache - first run will download packages'
            fi
            echo 'Build would run here: dotnet build'
        ");
    });
