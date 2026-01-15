// Test CLI injection - using ilmarinen-agent CLI from within containers
// Note: The CLI is a shell script that requires wget or curl in the container
// Alpine images have wget by default; for debian/ubuntu, install curl first

// Helper to assert output contains expected string
void AssertContains(string output, string expected, string message)
{
    if (!output.Contains(expected))
        throw new Exception($"{message}\nExpected to contain: {expected}\nActual: {output}");
}

Step("test-cli")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        Console.WriteLine("=== Testing ilmarinen-agent CLI injection ===\n");

        // Check if CLI is available
        // Note: Shell now throws automatically on failure
        Console.WriteLine("1. Checking CLI availability...");
        var result = await ctx.Shell("which ilmarinen-agent");
        AssertContains(result.Stdout, "/usr/local/bin/ilmarinen-agent", "CLI not found at expected path");

        result = await ctx.Shell("ilmarinen-agent --version");
        AssertContains(result.Stdout, "ilmarinen-agent", "Version output missing ilmarinen-agent");

        // Test help
        Console.WriteLine("2. Testing ilmarinen-agent --help...");
        result = await ctx.Shell("ilmarinen-agent --help");
        AssertContains(result.Stdout, "run <image>", "Help missing 'run' command");
        AssertContains(result.Stdout, "info <branch|commit>", "Help missing 'info' command");

        // Test info commands
        Console.WriteLine("3. Testing ilmarinen-agent info...");
        result = await ctx.Shell("ilmarinen-agent info branch");
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new Exception("Branch info returned empty");
        Console.WriteLine($"   Branch: {result.Stdout.Trim()}");

        result = await ctx.Shell("ilmarinen-agent info commit");
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new Exception("Commit info returned empty");
        Console.WriteLine($"   Commit: {result.Stdout.Trim()}");

        Console.WriteLine("\n=== CLI injection tests passed! ===");
    });

// Nested container operations using docker:latest (alpine-based, has wget)
Step("test-cli-nested")
    .Image("docker:latest")
    .Run(async ctx =>
    {
        Console.WriteLine("=== Testing nested containers via CLI ===\n");

        // Test running a nested container via CLI
        Console.WriteLine("1. Running nested container via CLI...");
        var result = await ctx.Shell("ilmarinen-agent run alpine -- echo 'Hello from nested!'");
        AssertContains(result.Stdout, "Hello from nested!", "Nested container output incorrect");

        // Test workspace sharing
        Console.WriteLine("2. Testing workspace sharing...");
        await ctx.Shell("echo 'test-data-from-parent' > /workspace/cli-test.txt");
        result = await ctx.Shell("ilmarinen-agent run alpine -- cat /workspace/cli-test.txt");
        AssertContains(result.Stdout, "test-data-from-parent", "Workspace sharing failed");
        await ctx.Shell("rm /workspace/cli-test.txt");

        Console.WriteLine("\n=== Nested container tests passed! ===");
    });
