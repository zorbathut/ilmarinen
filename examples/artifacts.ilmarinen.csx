// Example: Saving build artifacts
//
// Artifacts are files produced during pipeline execution that persist
// beyond the job. Use ctx.SaveArtifact() to save files for later retrieval.

Step("build")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        // Create some build outputs
        await ctx.Shell("""
            echo 'Building project...'
            mkdir -p dist
            echo '{"name": "myapp", "version": "1.0.0"}' > dist/manifest.json
            echo '#!/bin/sh\necho Hello from myapp!' > dist/myapp.sh
            chmod +x dist/myapp.sh
            tar -czf dist/release.tar.gz -C dist manifest.json myapp.sh
            echo 'Build complete!'
            """);

        // Save individual files as artifacts
        var manifest = await ctx.SaveArtifact("dist/manifest.json");
        Console.WriteLine($"Saved manifest: {manifest.Name} ({manifest.Size} bytes)");

        // Save with a custom name
        var tarball = await ctx.SaveArtifact("dist/release.tar.gz", "myapp-v1.0.0.tar.gz");
        Console.WriteLine($"Saved release: {tarball.Name} ({tarball.Size} bytes)");
    });

Step("test")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        // Generate a test report
        await ctx.Shell("""
            echo 'Running tests...'
            echo '<?xml version="1.0"?>' > test-results.xml
            echo '<testsuites tests="3" failures="0">' >> test-results.xml
            echo '  <testsuite name="unit" tests="3">' >> test-results.xml
            echo '    <testcase name="test_add"/>' >> test-results.xml
            echo '    <testcase name="test_subtract"/>' >> test-results.xml
            echo '    <testcase name="test_multiply"/>' >> test-results.xml
            echo '  </testsuite>' >> test-results.xml
            echo '</testsuites>' >> test-results.xml
            echo 'All tests passed!'
            """);

        await ctx.SaveArtifact("test-results.xml");
    });
