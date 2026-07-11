// Step Output Demo
//
// Demonstrates building an image in one step and using it in another. The first step returns an ImageRef which is used by the second step.

// Step 1: Build an image and return the ImageRef
var build = Step<ImageRef>("build")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        // Create a simple Dockerfile
        await ctx.Shell(@"
cat > /workspace/Dockerfile << 'EOF'
FROM alpine:latest
RUN echo 'Hello from the built image!' > /message.txt
CMD [""cat"", ""/message.txt""]
EOF
");

        Console.WriteLine("Building image...");
        var image = await ctx.BuildImage("Dockerfile", "step-output-demo:latest");
        Console.WriteLine($"Built: {image}");

        return image;
    });

// Step 2: Use the built image (lazy reference resolved at runtime)
Step("run")
    .Image(() => build.Output!)
    .Run(async ctx =>
    {
        Console.WriteLine("Running in the built image:");
        await ctx.Exec("cat", "/message.txt");
    });

// Step 3: Cleanup
Step("cleanup")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        await ctx.Shell("docker rmi step-output-demo:latest 2>/dev/null || true");
        await ctx.Shell("rm -f /workspace/Dockerfile");
        Console.WriteLine("Cleaned up!");
    });
