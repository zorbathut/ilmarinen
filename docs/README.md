# Conductor

A container-native CI/CD system with C# pipelines.

## Philosophy

- **Everything runs in containers.** No exceptions. No "agent any." Every step declares its image.
- **Pipelines are code.** Real C#, not YAML-with-escape-hatches. Full IDE support, type checking, NuGet packages.
- **DAGs are first-class.** Simple pipelines are simple. Complex workflows with dynamic dependencies are fully supported.
- **Images are values.** Build a container in one step, use it in the next. No magic, no side channels.

## Quick Start

```bash
# Install the CLI
dotnet tool install -g Conductor.Cli

# Initialize a new pipeline
conductor init

# Run locally
conductor run
```

## Your First Pipeline

Create `pipeline.csx` in your repo root:

```csharp
#r "Conductor"

var app = Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        await ctx.Exec("dotnet", "build", "-c", "Release");
        await ctx.Exec("dotnet", "publish", "-o", "out");
        return ctx.BuildImage("Dockerfile");
    });

Step("test")
    .Image(app)
    .Run(ctx => ctx.Exec("dotnet", "test"));

Step("deploy")
    .Image("bitnami/kubectl:latest")
    .Needs("test")
    .When(ctx => ctx.Branch == "main")
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/"));
```

That's it. Three steps: build (produces a container), test (runs in that container), deploy (conditional on branch).

## Core Concepts

### Steps

The basic unit of work. Every step runs in a container.

```csharp
Step("name")
    .Image("image:tag")           // Required: container image
    .Run(ctx => /* work */);      // Required: what to do
```

### Dependencies

Steps run in parallel by default. Use `Needs()` to create dependencies:

```csharp
Step("deploy")
    .Image("kubectl:latest")
    .Needs("test", "security-scan")  // Waits for both
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/"));
```

### Image Outputs

Build a container in one step, use it in another:

```csharp
var myImage = Step("build")
    .Image("docker:dind")
    .Run(ctx => ctx.BuildImage("Dockerfile"));

Step("test")
    .Image(myImage)  // Automatic dependency
    .Run(ctx => ctx.Exec("./run-tests.sh"));
```

### Services (Sidecars)

Run containers alongside your step:

```csharp
Step("integration-test")
    .Image("dotnet/sdk:8.0")
    .Service("postgres:15", "db")
    .Service(myAppImage, "app", ports: [8080])
    .Run(async ctx =>
    {
        await ctx.WaitForHealthy("http://app:8080/health");
        await ctx.Exec("dotnet", "test", "--filter", "Integration");
    });
```

### Artifacts

Pass files between steps:

```csharp
Step("build")
    .Image("dotnet/sdk:8.0")
    .Run(ctx =>
    {
        ctx.Exec("dotnet", "build");
        ctx.Output("binaries", "bin/**/*.dll");  // Named artifact
    });

Step("package")
    .Image("dotnet/sdk:8.0")
    .Needs("build")
    .Run(ctx =>
    {
        ctx.Input("binaries");  // Download artifact
        ctx.Exec("zip", "-r", "release.zip", "bin/");
    });
```

### Secrets

Secrets are injected as environment variables, never logged:

```csharp
Step("deploy")
    .Image("kubectl:latest")
    .Env("KUBECONFIG", ctx => ctx.Secret("kubeconfig-prod"))
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/"));
```

### Conditional Execution

```csharp
Step("deploy-prod")
    .Image("kubectl:latest")
    .When(ctx => ctx.Branch == "main" && ctx.EventType != TriggerEvent.PullRequest)
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/prod/"));
```

## Dynamic DAGs

For complex workflows, use `Plan()` to generate the execution graph at runtime:

```csharp
Plan(async ctx =>
{
    var graph = new Graph();
    var projects = await ctx.ReadJson<Project[]>("projects.json");

    foreach (var project in projects)
    {
        graph.Add($"build-{project.Name}",
            image: project.BuildImage,
            needs: project.Dependencies.Select(d => $"build-{d}"),
            run: ctx => ctx.Exec("dotnet", "build", project.Path));
    }

    return graph;
});
```

The planning phase itself runs in a container (specified by `PlanningImage`), ensuring reproducibility.

## Documentation

- [Complete Guide](GUIDE.md) — Full API reference and patterns
- [Examples](examples/) — Real-world pipeline examples

## Comparison with Jenkins

| Jenkins | Conductor |
|---------|-----------|
| `agent any` | Not allowed — must specify image |
| Groovy scripts | C# with full type checking |
| Plugin ecosystem | NuGet packages |
| `Jenkinsfile` | `pipeline.csx` or compiled assembly |
| `stage`/`steps` | `Step()` with fluent API |
| `post { failure { } }` | `OnFailure()` handler |
| `parallel` | Automatic — use `Needs()` for ordering |

## License

MIT
