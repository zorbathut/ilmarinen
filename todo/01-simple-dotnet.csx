// Simple .NET Application Pipeline
// 
// A straightforward build → test → deploy pipeline for a .NET application.
// This is the "hello world" of Conductor pipelines.

#r "Conductor"

// Build the application and create a container
var build = Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        await ctx.Exec("dotnet", "restore");
        await ctx.Exec("dotnet", "build", "-c", "Release");
        await ctx.Exec("dotnet", "publish", "-c", "Release", "-o", "out");

        // Build and return the container image
        return ctx.BuildImage("Dockerfile");
    });

// Run tests inside the built image
var test = Step("test")
    .Image(build)
    .Run(ctx => ctx.Exec("dotnet", "test", "--no-build", "-c", "Release"));

// Deploy to Kubernetes (only on main branch)
Step("deploy")
    .Image("bitnami/kubectl:latest")
    .Needs(test)
    .When(ctx => ctx.Branch == "main")
    .Env("KUBECONFIG", ctx => ctx.Secret("kubeconfig"))
    .Run(async ctx =>
    {
        await ctx.Exec("kubectl", "apply", "-f", "k8s/");
        await ctx.Exec("kubectl", "rollout", "status", "deployment/myapp", "--timeout=5m");
    });

// Notify on failure
OnFailure(async ctx =>
{
    var webhook = ctx.Secret("discord-webhook");
    await ctx.Discord(webhook).Send(new DiscordMessage
    {
        Title = "Build Failed",
        Description = $"Build failed: {ctx.JobName} #{ctx.BuildNumber}",
        Link = ctx.BuildUrl,
        Color = DiscordColor.Danger
    });
});
