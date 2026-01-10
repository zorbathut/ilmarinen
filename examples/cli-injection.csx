// Test CLI injection - using ilmarinen CLI from within containers
// Note: The CLI is a shell script that requires wget or curl in the container
// Alpine images have wget by default; for debian/ubuntu, install curl first

// Helper to run shell and throw on failure
async Task<string> Run(IJobContext ctx, string cmd)
{
    var result = await ctx.Shell(cmd);
    if (!result.Success)
        throw new Exception($"Command failed: {cmd}\nStderr: {result.Stderr}");
    return result.Stdout;
}

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
        Console.WriteLine("=== Testing ilmarinen CLI injection ===\n");

        // Check if CLI is available
        Console.WriteLine("1. Checking CLI availability...");
        var output = await Run(ctx, "which ilmarinen");
        AssertContains(output, "/usr/local/bin/ilmarinen", "CLI not found at expected path");

        output = await Run(ctx, "ilmarinen --version");
        AssertContains(output, "ilmarinen", "Version output missing ilmarinen");

        // Test help
        Console.WriteLine("2. Testing ilmarinen --help...");
        output = await Run(ctx, "ilmarinen --help");
        AssertContains(output, "run <image>", "Help missing 'run' command");
        AssertContains(output, "info <branch|commit>", "Help missing 'info' command");

        // Test info commands
        Console.WriteLine("3. Testing ilmarinen info...");
        output = await Run(ctx, "ilmarinen info branch");
        if (string.IsNullOrWhiteSpace(output))
            throw new Exception("Branch info returned empty");
        Console.WriteLine($"   Branch: {output.Trim()}");

        output = await Run(ctx, "ilmarinen info commit");
        if (string.IsNullOrWhiteSpace(output))
            throw new Exception("Commit info returned empty");
        Console.WriteLine($"   Commit: {output.Trim()}");

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
        var output = await Run(ctx, "ilmarinen run alpine -- echo 'Hello from nested!'");
        AssertContains(output, "Hello from nested!", "Nested container output incorrect");

        // Test workspace sharing
        Console.WriteLine("2. Testing workspace sharing...");
        await Run(ctx, "echo 'test-data-from-parent' > /workspace/cli-test.txt");
        output = await Run(ctx, "ilmarinen run alpine -- cat /workspace/cli-test.txt");
        AssertContains(output, "test-data-from-parent", "Workspace sharing failed");
        await Run(ctx, "rm /workspace/cli-test.txt");

        Console.WriteLine("\n=== Nested container tests passed! ===");
    });
