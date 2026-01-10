# ilmarinen Implementation Plan

## Overview

ilmarinen is a container-native CI/CD system with C# pipelines. This document outlines the implementation plan for v0.1.

## Scope Summary

### In scope for v0.1
- Basic steps with `Step()`, `.Image()`, `.Run()`, `.Needs()`
- DAG-based parallel execution
- `.csx` script format (Roslyn scripting)
- Environment-based secrets
- Local filesystem artifacts
- Docker as execution runtime
- **ilmarinen CLI** injected into containers for nested orchestration
- Basic `ctx.Exec()`, `ctx.Secret()`, `ctx.Output()`, `ctx.Input()`

### Deferred to later versions
- Service sidecars (`.Service()`, health checks)
- Dynamic DAGs (`Plan()`)
- Compiled assembly format
- Approvals, notifications
- Web UI / server mode

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        ilmarinen CLI                            │
│  (Host)         ilmarinen run pipeline.csx                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐      │
│  │ Script Engine│───▶│  DAG Builder │───▶│  Executor    │      │
│  │   (Roslyn)   │    │              │    │  (Docker)    │      │
│  └──────────────┘    └──────────────┘    └──────────────┘      │
│                                                 │                │
│                                                 ▼                │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                    Artifact Store                         │  │
│  │                (local filesystem)                         │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                              │
                              │ Docker API
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Step Containers                             │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐             │
│  │   build     │  │    test     │  │   deploy    │             │
│  │  (parallel) │  │  (parallel) │  │ (sequential)│             │
│  │             │  │             │  │             │             │
│  │ /ilmarinen ◀┼──┼─────────────┼──┼── mounted   │             │
│  │ /workspace ◀┼──┼─────────────┼──┼── mounted   │             │
│  └─────────────┘  └─────────────┘  └─────────────┘             │
└─────────────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
ilmarinen/
├── src/
│   ├── Ilmarinen.Core/              # Domain models, interfaces
│   │   ├── Models/
│   │   │   ├── Step.cs
│   │   │   ├── StepDefinition.cs
│   │   │   ├── ExecutionGraph.cs
│   │   │   ├── ImageRef.cs
│   │   │   └── BuildContext.cs
│   │   ├── Execution/
│   │   │   ├── IExecutor.cs
│   │   │   ├── IJobContext.cs
│   │   │   └── ExecutionResult.cs
│   │   └── Artifacts/
│   │       └── IArtifactStore.cs
│   │
│   ├── Ilmarinen.Scripting/         # Roslyn script engine
│   │   ├── ScriptHost.cs            # DSL globals (Step, OnFailure, etc.)
│   │   ├── PipelineScript.cs
│   │   └── ScriptCompiler.cs
│   │
│   ├── Ilmarinen.Docker/            # Docker execution engine
│   │   ├── DockerExecutor.cs
│   │   ├── DockerJobContext.cs
│   │   └── ContainerBuilder.cs
│   │
│   ├── Ilmarinen.Cli/               # Main CLI (host-side)
│   │   └── Program.cs
│   │
│   └── Ilmarinen.Agent/             # Injected CLI (container-side)
│       ├── Program.cs
│       └── Commands/
│           ├── BuildCommand.cs
│           ├── RunCommand.cs
│           ├── SecretCommand.cs
│           ├── ArtifactCommand.cs
│           └── InfoCommand.cs
│
├── tests/
│   ├── Ilmarinen.Core.Tests/
│   ├── Ilmarinen.Scripting.Tests/
│   └── Ilmarinen.Docker.Tests/
│
└── Ilmarinen.sln
```

---

## Implementation Phases

### Phase 1: Core Domain Model

**Goal:** Define the fundamental types that represent pipelines and steps.

**Deliverables:**
- `Step`, `StepBuilder` - fluent API for step definition
- `StepDefinition` - immutable step configuration
- `ExecutionGraph` - DAG representation with topological sorting
- `ImageRef` - container image reference
- `IJobContext` interface - what steps interact with
- `ExecutionResult` - step outcomes

**Key types:**

```csharp
// Fluent step builder
public class StepBuilder
{
    public StepBuilder Image(string image);
    public StepBuilder Image(Step<ImageRef> imageStep);
    public StepBuilder Needs(params Step[] steps);
    public StepBuilder Needs(params string[] stepNames);
    public StepBuilder When(Func<IConditionContext, bool> condition);
    public StepBuilder Env(string key, string value);
    public Step Run(Func<IJobContext, Task> action);
    public Step<T> Run<T>(Func<IJobContext, Task<T>> action);
}

// Immutable step definition
public record StepDefinition
{
    public string Name { get; init; }
    public string Image { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; }
    public Func<IJobContext, Task<object?>> Action { get; init; }
    public Func<IConditionContext, bool>? Condition { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
}
```

---

### Phase 2: Script Engine (Roslyn)

**Goal:** Parse and execute `.csx` pipeline scripts.

**Deliverables:**
- `ScriptHost` - provides `Step()`, `OnFailure()` globals
- `ScriptCompiler` - loads and compiles `.csx` files
- Reference handling (`#r "Ilmarinen"`)
- Extract defined steps into `ExecutionGraph`

**How it works:**

1. User writes `pipeline.csx` with `#r "Ilmarinen"` reference
2. Script engine provides globals: `Step()`, `OnFailure()`, etc.
3. Running the script populates a `PipelineDefinition`
4. Steps are collected and dependencies resolved into an `ExecutionGraph`

```csharp
// Script globals provided to .csx files
public class ScriptHost
{
    public StepBuilder Step(string name);
    public void OnFailure(Func<IPipelineContext, Task> handler);
    public void OnSuccess(Func<IPipelineContext, Task> handler);
}
```

---

### Phase 3: Docker Executor

**Goal:** Execute steps as Docker containers with proper dependency ordering.

**Deliverables:**
- `DockerExecutor` - runs steps as Docker containers
- `DockerJobContext` - implements `IJobContext` for containerized execution
- Workspace mounting (shared volume)
- ilmarinen agent binary mounting
- Parallel execution of independent steps
- Sequential execution of dependent steps

**Execution algorithm:**

```
1. Build ExecutionGraph from pipeline definition
2. Identify steps with no dependencies (ready set)
3. While steps remain:
   a. Run all ready steps in parallel
   b. Wait for any to complete
   c. Mark completed, check if dependents are now ready
   d. Add newly ready steps to ready set
4. Collect results, report failures
```

**Container setup:**

```csharp
// For each step container:
docker run \
  -v /workspace:/workspace \
  -v /path/to/ilmarinen-agent:/usr/local/bin/ilmarinen:ro \
  -e ILMARINEN_API=http://host.docker.internal:8765 \
  -e ILMARINEN_STEP=step-name \
  -e ILMARINEN_BUILD_ID=abc123 \
  --network ilmarinen-net \
  <image> \
  <entrypoint>
```

---

### Phase 4: Artifact Store

**Goal:** Enable file passing between steps.

**Deliverables:**
- `LocalArtifactStore` - filesystem-based implementation
- `ctx.Output(name, glob)` - capture files
- `ctx.Input(name)` - retrieve files
- Automatic cleanup after pipeline completion

**Storage layout:**

```
.ilmarinen/
├── artifacts/
│   └── <build-id>/
│       ├── <step-name>/
│       │   └── <artifact-name>/
│       │       └── ... files ...
│       └── ...
└── workspace/
    └── <build-id>/
        └── ... shared workspace ...
```

---

### Phase 5: ilmarinen Agent CLI

**Goal:** Provide the binary injected into containers for nested orchestration.

**Deliverables:**
- Separate executable built for Linux (static binary via `PublishAot` or `PublishSingleFile`)
- Multi-arch builds (amd64, arm64)
- Commands: `build`, `run`, `secret`, `artifact`, `info`
- HTTP API communication with host coordinator

**Commands:**

```bash
# Build an image
ilmarinen build -f Dockerfile -t myapp:latest

# Run a container
ilmarinen run myapp:latest -- ./command.sh

# Get a secret
ilmarinen secret get db-password

# Output artifacts
ilmarinen artifact output ./results --name test-results

# Input artifacts
ilmarinen artifact input build-output --dest ./artifacts

# Get build info
ilmarinen info branch
ilmarinen info commit
```

**Communication:**

The agent CLI communicates with the host coordinator via HTTP:

```
GET  /api/secret/{name}
POST /api/artifact/output  { name, path }
POST /api/artifact/input   { name, dest }
GET  /api/info/{key}
POST /api/build            { dockerfile, tag, ... }
POST /api/run              { image, command, ... }
```

---

### Phase 6: Main CLI

**Goal:** User-facing command-line interface.

**Deliverables:**
- `ilmarinen run [pipeline.csx]` - execute a pipeline
- `ilmarinen init` - scaffold a new pipeline
- `ilmarinen validate` - check pipeline without running
- Progress output and logging

**Usage:**

```bash
# Run the default pipeline.csx in current directory
ilmarinen run

# Run a specific pipeline
ilmarinen run ci/build.csx

# Validate without executing
ilmarinen validate pipeline.csx

# Initialize a new pipeline
ilmarinen init
```

---

### Phase 7: Integration & Polish

**Goal:** Production-ready v0.1 release.

**Deliverables:**
- End-to-end integration tests
- Error handling and user-friendly messages
- Documentation (README, examples)
- CI/CD for ilmarinen itself (dogfooding)

---

## Key Design Decisions

### 1. Step execution model

Steps are lambdas defined in the script that run on the host. The lambda receives an `IJobContext` that dispatches operations to the running container via the ilmarinen agent.

```csharp
Step("build")
    .Image("dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        // This lambda runs on host
        // ctx.Exec() sends command to container via agent
        await ctx.Exec("dotnet", "build");
    });
```

### 2. ilmarinen agent communication

The host coordinator starts an HTTP server on a random port. Each container gets `ILMARINEN_API=http://host.docker.internal:PORT` (or `http://172.17.0.1:PORT` on Linux). The agent CLI calls this API for all operations.

This avoids:
- Complex inter-container networking
- Unix socket mounting issues
- gRPC complexity for v0.1

### 3. Workspace sharing

A single workspace directory is bind-mounted at `/workspace` in all step containers. This is the repo checkout. Artifacts are copied to a separate artifacts directory outside the workspace.

### 4. DAG execution

1. Topologically sort steps
2. Maintain sets: `pending`, `ready`, `running`, `completed`, `failed`
3. Run all `ready` steps in parallel (up to concurrency limit)
4. On completion, move to `completed`/`failed`, update dependents
5. Continue until all done or failure (with configurable fail-fast)

### 5. Image building

For v0.1, `ctx.BuildImage()` shells out to `docker build` from the host. The built image is available to subsequent steps. Later versions may support Kaniko, BuildKit, etc.

---

## Dependencies

### NuGet packages

- `Microsoft.CodeAnalysis.CSharp.Scripting` - Roslyn scripting
- `Docker.DotNet` - Docker API client
- `System.CommandLine` - CLI parsing
- `Microsoft.Extensions.Logging` - Logging abstractions
- `Spectre.Console` - Pretty console output

### External

- Docker daemon (or compatible runtime)
- .NET 8.0 SDK

---

## Success Criteria for v0.1

1. **Can run the simple .NET pipeline** (`01-simple-dotnet.csx`)
   - Build step runs in `dotnet/sdk:8.0`
   - Test step depends on build
   - Deploy step conditional on branch

2. **Parallel execution works**
   - Independent steps run concurrently
   - Dependencies are respected

3. **Artifacts work**
   - `ctx.Output()` captures files
   - `ctx.Input()` retrieves them in dependent steps

4. **Secrets work**
   - `ctx.Secret("name")` reads from environment

5. **ilmarinen agent works**
   - Can run `ilmarinen info branch` inside container
   - Can run `ilmarinen secret get` inside container

---

## Future Phases (post v0.1)

### v0.2: Services
- `.Service()` for sidecars
- Health checks (`ctx.WaitForHealthy()`)
- Service networking

### v0.3: Dynamic DAGs
- `Plan()` function
- `IPlanContext`
- Planning phase execution

### v0.4: Compiled Pipelines
- Assembly-based pipeline format
- `[Pipeline]` attribute
- Unit testing pipelines

### v0.5: Server Mode
- Central coordinator
- Web UI
- Webhooks for triggers
- Build history

---

## Open Questions

1. **Container image building:** Should v0.1 support `ctx.BuildImage()` or defer it? (Current plan: include it, shell out to docker build)

2. **Windows support:** Focus on Linux containers first? (Current plan: yes, Linux-first)

3. **Logging:** Stream container logs in real-time or buffer? (Current plan: stream with prefix per step)
