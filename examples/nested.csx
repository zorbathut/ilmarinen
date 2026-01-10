// Nested container operations demo

Step("nested-containers")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        // Create a test file in the workspace
        await ctx.Shell("echo 'Hello from parent!' > /workspace/test.txt");

        // Test 1: Run a nested container that can see the workspace
        Console.WriteLine("=== Test 1: Nested container with shared workspace ===");
        var result = await ctx.Run("alpine:latest", "cat", "/workspace/test.txt");
        Console.WriteLine($"Exit code: {result.ExitCode}");

        // Test 2: Nested container can write to workspace
        Console.WriteLine("\n=== Test 2: Nested container writes to workspace ===");
        await ctx.Run("alpine:latest", "sh", "-c", "echo 'Written by nested!' >> /workspace/test.txt");
        await ctx.Shell("cat /workspace/test.txt");

        // Test 3: Build and run a custom image
        Console.WriteLine("\n=== Test 3: Build and run custom image ===");
        await ctx.Shell(@"
cat > /workspace/Dockerfile.test << 'EOF'
FROM alpine:latest
COPY test.txt /app/
CMD [""cat"", ""/app/test.txt""]
EOF
");
        var image = await ctx.BuildImage("/workspace/Dockerfile.test", "nested-test:latest");
        Console.WriteLine($"Built image: {image}");

        await ctx.Run(image, "cat", "/app/test.txt");

        // Cleanup
        await ctx.Shell("rm /workspace/test.txt /workspace/Dockerfile.test");
        await ctx.Shell("docker rmi nested-test:latest 2>/dev/null || true");

        Console.WriteLine("\n=== All nested container tests passed! ===");
    });
