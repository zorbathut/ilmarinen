// Simple hello world pipeline

Step("hello")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        await ctx.Exec("echo", "Hello from ilmarinen!");
        await ctx.Shell("echo 'Current directory:' && pwd && ls -la");
    });
