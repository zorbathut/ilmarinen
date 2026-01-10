# Conductor Guide

Complete reference for building pipelines with Conductor.

## Table of Contents

1. [Pipeline Formats](#pipeline-formats)
2. [Step API](#step-api)
3. [Job Context API](#job-context-api)
4. [Dynamic DAGs](#dynamic-dags)
5. [Container Images](#container-images)
6. [Services and Sidecars](#services-and-sidecars)
7. [Artifacts](#artifacts)
8. [Secrets](#secrets)
9. [Notifications](#notifications)
10. [Approvals](#approvals)
11. [Matrix Builds](#matrix-builds)
12. [Error Handling](#error-handling)

---

## Pipeline Formats

Conductor supports two formats: C# scripts (`.csx`) and compiled assemblies.

### Script Format (Recommended for Simple Pipelines)

Create `pipeline.csx` in your repo root:

```csharp
#r "Conductor"

Step("build")
    .Image("dotnet/sdk:8.0")
    .Run(ctx => ctx.Exec("dotnet", "build"));

Step("test")
    .Image("dotnet/sdk:8.0")
    .Needs("build")
    .Run(ctx => ctx.Exec("dotnet", "test"));
```

Scripts use [C# Scripting](https://github.com/dotnet/roslyn/wiki/Scripting-API-Samples) via Roslyn. Top-level statements, no boilerplate.

### Compiled Format (For Complex Pipelines)

Create a class library with a `Pipeline` subclass:

```csharp
using Conductor;

[Pipeline("my-pipeline")]
[PlanningImage("mcr.microsoft.com/dotnet/sdk:8.0")]
public class MyPipeline : Pipeline
{
    [Secret("discord-webhook")]
    private readonly string _discordWebhook;

    public override async Task<ExecutionGraph> Plan(IPlanContext ctx)
    {
        var graph = new Graph();
        // Build your DAG here
        return graph;
    }

    [OnFailure]
    public async Task NotifyFailure(IPipelineContext ctx)
    {
        await ctx.Discord(_discordWebhook).Send(new DiscordMessage
        {
            Title = "Build Failed",
            Description = $"Build failed: {ctx.JobName} #{ctx.BuildNumber}"
        });
    }
}
```

Benefits: Full IDE support, unit testing, complex logic, NuGet dependencies.

---

## Step API

### Basic Step

```csharp
Step("name")
    .Image("image:tag")
    .Run(ctx => ctx.Exec("command", "args"));
```

### Fluent Methods

| Method | Description |
|--------|-------------|
| `.Image(string)` | Container image (required) |
| `.Image(ImageRef)` | Container from previous step |
| `.Run(Func<IJobContext, Task>)` | Work to execute (required) |
| `.Needs(params string[])` | Dependencies by name |
| `.When(Func<IConditionContext, bool>)` | Conditional execution |
| `.Env(string, string)` | Environment variable |
| `.Env(string, Func<IJobContext, string>)` | Dynamic environment variable |
| `.Service(string, string, int[]?)` | Add sidecar container |
| `.Service(ImageRef, string, int[]?)` | Add sidecar from built image |
| `.Resources(cpu, memory)` | Resource limits |
| `.Timeout(TimeSpan)` | Maximum execution time |
| `.Retry(int, TimeSpan?)` | Retry on failure |
| `.DisplayName(string)` | Human-readable name for UI |

### Returning Values

Steps can return values for use in later steps:

```csharp
var version = Step("get-version")
    .Image("alpine")
    .Run(async ctx =>
    {
        var result = await ctx.Exec("cat", "VERSION");
        return result.Stdout.Trim();
    });

Step("tag")
    .Image("docker:dind")
    .Run(ctx => ctx.Exec("docker", "tag", "app", $"app:{version.Value}"));
```

### Returning Images

```csharp
var appImage = Step("build")
    .Image("docker:dind")
    .Run(ctx => ctx.BuildImage("Dockerfile", tag: "myapp:${BUILD_ID}"));

// Use the built image
Step("test")
    .Image(appImage)  // Runs inside the image you just built
    .Run(ctx => ctx.Exec("./run-tests.sh"));
```

---

## Job Context API

The `IJobContext` is passed to your `Run` function and provides all interactions with the container.

### Command Execution

```csharp
// Simple execution
await ctx.Exec("dotnet", "build");

// Capture output
var result = await ctx.Exec("git", "rev-parse", "HEAD");
Console.WriteLine(result.Stdout);  // The commit hash

// Shell scripts
await ctx.Shell("echo $HOME && ls -la");

// With working directory
await ctx.Exec("npm", "install").WorkingDirectory("frontend/");

// With environment
await ctx.Exec("deploy.sh").Env("TARGET", "production");

// Ignore exit code
await ctx.Exec("grep", "pattern", "file.txt").AllowFailure();
```

### File Operations

```csharp
// Read files
var config = await ctx.ReadJson<Config>("config.json");
var content = await ctx.ReadText("VERSION");
var lines = await ctx.ReadLines("hosts.txt");

// Write files
await ctx.WriteText("output.txt", "content");
await ctx.WriteJson("data.json", myObject);

// Glob patterns
var files = ctx.Glob("src/**/*.cs");

// Check existence
if (ctx.FileExists("optional-config.json")) { }
```

### Artifacts

```csharp
// Output artifacts (available to downstream steps)
ctx.Output("binaries", "bin/**/*.dll");
ctx.Output("packages", "*.nupkg", fingerprint: true);

// Input artifacts (from upstream steps)
var path = ctx.Input("binaries");  // Returns local path
ctx.Input("binaries", "lib/");     // Extract to specific directory
```

### Container Building

```csharp
// Build from Dockerfile
var image = await ctx.BuildImage("Dockerfile");

// With options
var image = await ctx.BuildImage("Dockerfile", new BuildOptions
{
    Tag = "myapp:${BUILD_ID}",
    BuildArgs = new Dictionary<string, string>
    {
        ["VERSION"] = "1.0.0"
    },
    Target = "runtime",  // Multi-stage target
    CacheFrom = ["myapp:cache"]
});

// Build from tarball (for kaniko, etc.)
var image = ctx.OutputImage("image.tar", "myapp:latest");
```

### Secrets

```csharp
var apiKey = ctx.Secret("api-key");  // Injected, never logged
```

### Metadata

```csharp
ctx.JobName        // "build"
ctx.BuildId        // "12345"
ctx.BuildNumber    // 42
ctx.Branch         // "main"
ctx.Commit         // "abc123..."
ctx.CommitMessage  // "Fix bug"
ctx.EventType      // TriggerEvent.Push, PullRequest, etc.
ctx.PullRequestId  // "123" (if applicable)
ctx.BuildUrl       // "https://conductor.io/builds/12345"
ctx.RepoUrl        // "https://github.com/org/repo"
```

---

## Dynamic DAGs

For pipelines that need to discover work at runtime.

### Basic Structure

```csharp
Plan(async ctx =>
{
    var graph = new Graph();
    
    // Add jobs dynamically
    graph.Add("job-name",
        image: "image:tag",
        run: ctx => ctx.Exec("command"));
    
    return graph;
});
```

### Plan Context

The `IPlanContext` provides read-only access during planning:

```csharp
Plan(async ctx =>
{
    // Read repo files
    var manifest = await ctx.ReadJson<Manifest>("manifest.json");
    var files = ctx.Glob("projects/**/project.json");
    
    // Execute discovery commands
    var affected = await ctx.Exec("nx", "affected:list", "--plain");
    var projects = affected.Stdout.Split('\n');
    
    // Access trigger info
    if (ctx.Branch == "main") { }
    if (ctx.EventType == TriggerEvent.PullRequest) { }
    
    // Access PR info
    var changedFiles = ctx.PullRequest?.ChangedFiles ?? [];
});
```

### Graph API

```csharp
var graph = new Graph();

// Add a job
var build = graph.Add("build",
    image: "dotnet/sdk:8.0",
    run: ctx => ctx.Exec("dotnet", "build"));

// Add with dependencies
graph.Add("test",
    image: "dotnet/sdk:8.0",
    needs: ["build"],
    run: ctx => ctx.Exec("dotnet", "test"));

// Add with services
graph.Add("integration",
    image: "dotnet/sdk:8.0",
    needs: ["containerize"],
    services: [new Service("postgres:15", "db")],
    run: async ctx =>
    {
        await ctx.WaitForHealthy("http://db:5432");
        await ctx.Exec("dotnet", "test", "--filter", "Integration");
    });

// Conditional jobs
if (ctx.Branch == "main")
{
    graph.Add("deploy",
        image: "kubectl:latest",
        needs: ["test", "integration"],
        run: ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/"));
}

// Capture image outputs
ImageRef appImage = null;
graph.Add("containerize",
    image: "docker:dind",
    needs: ["build"],
    run: ctx => ctx.BuildImage("Dockerfile"),
    capture: img => appImage = img);

// Use captured image later
graph.Add("scan",
    image: "trivy:latest",
    needs: ["containerize"],
    run: ctx => ctx.Exec("trivy", "image", appImage));
```

### Dependencies

```csharp
// String-based (resolved by name)
graph.Add("deploy", needs: ["build", "test"], ...);

// Array of names
var prereqs = new[] { "build-a", "build-b", "build-c" };
graph.Add("integration", needs: prereqs, ...);

// Programmatic
foreach (var svc in services)
{
    graph.Add($"build-{svc.Name}", ...);
}
graph.Add("deploy", needs: services.Select(s => $"build-{s.Name}"), ...);
```

---

## Container Images

### From Registry

```csharp
Step("build").Image("mcr.microsoft.com/dotnet/sdk:8.0")
Step("test").Image("node:20-alpine")
Step("deploy").Image("bitnami/kubectl:1.28")
```

### From Previous Step

```csharp
var myImage = Step("build")
    .Image("docker:dind")
    .Run(ctx => ctx.BuildImage("Dockerfile"));

Step("test")
    .Image(myImage);  // Implicit dependency created
```

### ImageRef Type

```csharp
public record ImageRef
{
    public string Reference { get; }      // "myapp:abc123"
    public string? Digest { get; }        // "sha256:..."
    public JobNode? ProducedBy { get; }   // Implicit dependency
    
    // Implicit conversion from string
    public static implicit operator ImageRef(string tag);
}
```

### Building Images

```csharp
// Standard Docker build
var image = await ctx.BuildImage("Dockerfile");

// With options
var image = await ctx.BuildImage("src/Dockerfile", new BuildOptions
{
    Context = "src/",
    Tag = "myapp:${BUILD_ID}",
    BuildArgs = { ["VERSION"] = "1.0.0" },
    Target = "runtime",
    Labels = { ["git.commit"] = ctx.Commit },
    CacheFrom = ["myapp:cache"],
    Platform = "linux/amd64"
});

// From tarball (kaniko output)
var image = ctx.OutputImage("image.tar", "myapp:latest");
```

### Pushing Images

```csharp
Step("push")
    .Image("gcr.io/go-containerregistry/crane:latest")
    .Run(async ctx =>
    {
        await ctx.Push(appImage, "registry.io/myapp:latest");
        await ctx.Push(appImage, $"registry.io/myapp:{ctx.Commit}");
    });
```

---

## Services and Sidecars

Run containers alongside your step.

### Basic Service

```csharp
Step("test")
    .Image("dotnet/sdk:8.0")
    .Service("postgres:15", "db")  // Hostname: db
    .Run(async ctx =>
    {
        await ctx.WaitForHealthy("tcp://db:5432");
        await ctx.Exec("dotnet", "test");
    });
```

### Multiple Services

```csharp
Step("integration")
    .Image("dotnet/sdk:8.0")
    .Service("postgres:15", "db")
    .Service("redis:7", "cache")
    .Service("localstack/localstack", "aws")
    .Run(async ctx =>
    {
        await Task.WhenAll(
            ctx.WaitForHealthy("tcp://db:5432"),
            ctx.WaitForHealthy("tcp://cache:6379"),
            ctx.WaitForHealthy("http://aws:4566/_localstack/health")
        );
        await ctx.Exec("dotnet", "test", "--filter", "Integration");
    });
```

### Service from Built Image

```csharp
var appImage = Step("build")
    .Image("docker:dind")
    .Run(ctx => ctx.BuildImage("Dockerfile"));

Step("e2e")
    .Image("playwright:latest")
    .Service(appImage, "app", ports: [8080])
    .Run(async ctx =>
    {
        await ctx.WaitForHealthy("http://app:8080/health");
        await ctx.Exec("playwright", "test");
    });
```

### Service Configuration

```csharp
Step("test")
    .Image("dotnet/sdk:8.0")
    .Service(new ServiceConfig
    {
        Image = "postgres:15",
        Hostname = "db",
        Ports = [5432],
        Environment = new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = "test",
            ["POSTGRES_DB"] = "testdb"
        },
        HealthCheck = new HealthCheck
        {
            Command = ["pg_isready", "-U", "postgres"],
            Interval = TimeSpan.FromSeconds(5),
            Retries = 10
        }
    })
    .Run(ctx => ctx.Exec("dotnet", "test"));
```

### Health Checks

```csharp
// TCP port check
await ctx.WaitForHealthy("tcp://db:5432");

// HTTP endpoint
await ctx.WaitForHealthy("http://app:8080/health");

// HTTP with options
await ctx.WaitForHealthy("http://app:8080/health", new HealthCheckOptions
{
    Timeout = TimeSpan.FromMinutes(2),
    Interval = TimeSpan.FromSeconds(5),
    ExpectedStatus = 200
});

// Custom check
await ctx.WaitFor(async () =>
{
    var result = await ctx.Exec("pg_isready", "-h", "db").AllowFailure();
    return result.ExitCode == 0;
});
```

---

## Artifacts

Pass files between steps.

### Producing Artifacts

```csharp
Step("build")
    .Image("dotnet/sdk:8.0")
    .Run(ctx =>
    {
        ctx.Exec("dotnet", "build", "-c", "Release");
        
        // Named artifact with glob pattern
        ctx.Output("binaries", "bin/Release/**/*.dll");
        
        // Multiple artifacts
        ctx.Output("packages", "**/*.nupkg");
        ctx.Output("docs", "docs/_site/**/*");
        
        // With fingerprinting (for caching/verification)
        ctx.Output("release", "release.zip", fingerprint: true);
    });
```

### Consuming Artifacts

```csharp
Step("deploy")
    .Image("alpine")
    .Needs("build")
    .Run(ctx =>
    {
        // Download to current directory
        var path = ctx.Input("binaries");
        
        // Download to specific path
        ctx.Input("packages", "packages/");
        
        // From specific upstream job (when multiple produce same name)
        ctx.Input("binaries", from: "build-linux");
    });
```

### Wildcard Inputs

```csharp
Step("aggregate")
    .Image("alpine")
    .Needs("build-linux", "build-windows", "build-mac")
    .Run(ctx =>
    {
        // Get artifacts from all matching jobs
        ctx.Input("binaries", from: "build-*");
    });
```

---

## Secrets

Secrets are injected securely and never appear in logs.

### Defining Secrets

Secrets are configured in the Conductor UI or via API, then referenced by name.

### Using Secrets

```csharp
// In Step context
Step("deploy")
    .Image("kubectl:latest")
    .Env("KUBECONFIG_DATA", ctx => ctx.Secret("kubeconfig-prod"))
    .Run(async ctx =>
    {
        // Write secret to file for kubectl
        var kubeconfig = ctx.Secret("kubeconfig-prod");
        await ctx.WriteText("/tmp/kubeconfig", kubeconfig);
        await ctx.Exec("kubectl", "apply", "-f", "k8s/")
            .Env("KUBECONFIG", "/tmp/kubeconfig");
    });

// In Pipeline class (compiled format)
[Pipeline("deploy")]
public class DeployPipeline : Pipeline
{
    [Secret("discord-webhook")]
    private readonly string _webhook;
    
    [Secret("npm-token")]
    private readonly string _npmToken;
}
```

### Secret Masking

All secret values are automatically masked in logs:

```
Running: curl -H "Authorization: Bearer ****"
```

---

## Notifications

### Discord

```csharp
[OnFailure]
public async Task NotifyFailure(IPipelineContext ctx)
{
    await ctx.Discord(webhookUrl).Send(new DiscordMessage
    {
        Title = "Build Failed",
        Description = $"{ctx.JobName} #{ctx.BuildNumber}",
        Link = ctx.BuildUrl,
        Color = DiscordColor.Danger,
        Fields = [
            new("Branch", ctx.Branch),
            new("Commit", ctx.Commit[..8])
        ]
    });
}

[OnSuccess]
public async Task NotifySuccess(IPipelineContext ctx)
{
    await ctx.Discord(webhookUrl).Send(new DiscordMessage
    {
        Title = "Build Succeeded",
        Color = DiscordColor.Success
    });
}
```

### Slack

```csharp
await ctx.Slack(webhookUrl).Send(new SlackMessage
{
    Text = $"Build {ctx.BuildNumber} completed",
    Blocks = [
        new SectionBlock($"*{ctx.JobName}* #{ctx.BuildNumber}"),
        new ContextBlock($"Branch: {ctx.Branch} | Commit: {ctx.Commit[..8]}")
    ]
});
```

### Email

```csharp
await ctx.Email(smtpConfig).Send(new EmailMessage
{
    To = ["team@example.com"],
    Subject = $"Build Failed: {ctx.JobName}",
    Body = $"Build #{ctx.BuildNumber} failed.\n\nSee: {ctx.BuildUrl}"
});
```

### Custom Webhooks

```csharp
await ctx.Webhook(url).Post(new
{
    event_type = "build_complete",
    build_id = ctx.BuildId,
    status = "success"
});
```

---

## Approvals

Gate deployments with manual approval.

### In Script Format

```csharp
Step("approve-prod")
    .Approval(new ApprovalConfig
    {
        Required = true,
        Approvers = ["platform-team", "oncall"],
        Timeout = TimeSpan.FromHours(24),
        Message = "Approve production deployment?"
    });

Step("deploy-prod")
    .Image("kubectl:latest")
    .Needs("approve-prod")
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/prod/"));
```

### In Dynamic DAGs

```csharp
graph.Add("approve-production",
    needs: ["verify-canary"],
    approval: new ApprovalConfig
    {
        Required = true,
        Approvers = ["platform-team"],
        Timeout = TimeSpan.FromHours(24)
    });

graph.Add("deploy-production",
    image: "kubectl:latest",
    needs: ["approve-production"],
    run: ctx => ctx.Exec("kubectl", "apply", "-k", "k8s/overlays/prod"));
```

---

## Matrix Builds

Run the same job across multiple configurations.

### In Script Format

```csharp
var platforms = new[] { "linux-x64", "win-x64", "osx-arm64" };

var builds = platforms.Select(platform =>
    Step($"build-{platform}")
        .Image("dotnet/sdk:8.0")
        .Run(ctx => ctx.Exec("dotnet", "publish", "-r", platform))
).ToList();

Step("release")
    .Image("alpine")
    .Needs(platforms.Select(p => $"build-{p}").ToArray())
    .Run(ctx => ctx.Exec("echo", "All platforms built"));
```

### In Dynamic DAGs

```csharp
Plan(async ctx =>
{
    var graph = new Graph();
    
    var matrix = from platform in new[] { "linux-x64", "win-x64", "osx-arm64" }
                 from config in new[] { "Debug", "Release" }
                 select new { platform, config };
    
    var buildJobs = matrix.Select(m =>
        graph.Add($"build-{m.platform}-{m.config}",
            image: "dotnet/sdk:8.0",
            run: ctx => ctx.Exec("dotnet", "publish",
                "-r", m.platform,
                "-c", m.config))
    ).ToList();
    
    // Fan-in after all matrix jobs
    graph.Add("aggregate",
        image: "alpine",
        needs: buildJobs.Select(j => j.Name),
        run: ctx => ctx.Exec("echo", "All builds complete"));
    
    return graph;
});
```

---

## Error Handling

### Retry

```csharp
Step("flaky-test")
    .Image("dotnet/sdk:8.0")
    .Retry(3, delay: TimeSpan.FromSeconds(30))
    .Run(ctx => ctx.Exec("dotnet", "test"));
```

### Timeout

```csharp
Step("long-running")
    .Image("alpine")
    .Timeout(TimeSpan.FromMinutes(30))
    .Run(ctx => ctx.Exec("./slow-process.sh"));
```

### Allow Failure

```csharp
Step("optional-lint")
    .Image("node:20")
    .AllowFailure()  // Pipeline continues even if this fails
    .Run(ctx => ctx.Exec("npm", "run", "lint"));
```

### Command-Level Failure Handling

```csharp
Step("resilient")
    .Image("alpine")
    .Run(async ctx =>
    {
        // Ignore specific command failure
        var result = await ctx.Exec("grep", "pattern", "file.txt").AllowFailure();
        
        if (result.ExitCode != 0)
        {
            Console.WriteLine("Pattern not found, using default");
        }
        
        // Retry within step
        await ctx.Retry(5, TimeSpan.FromSeconds(10), async () =>
        {
            await ctx.Exec("curl", "-f", "http://flaky-service/health");
        });
    });
```

### OnFailure Hook

```csharp
// Script format
OnFailure(async ctx =>
{
    await ctx.Discord(webhook).Send(new DiscordMessage
    {
        Title = "Build Failed",
        Description = $"{ctx.FailedJob}: {ctx.FailureMessage}"
    });
});

// Compiled format
[OnFailure]
public async Task HandleFailure(IPipelineContext ctx)
{
    // Notification, cleanup, etc.
}
```
