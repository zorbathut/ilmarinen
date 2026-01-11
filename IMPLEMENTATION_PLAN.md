# ilmarinen Implementation Plan

## Overview

ilmarinen is a container-native CI/CD system with C# pipelines. This document outlines the remaining work for v0.1.

## Current State

### Completed
- Core domain model: `Step<T>`, `StepBuilder<T>`, `ImageRef`, `IJobContext`
- Roslyn script engine for `.csx` pipelines
- Docker executor with workspace mounting
- ilmarinen agent CLI (shell script injected into containers)
- Commands: `build`, `run`, `secret get`, `info`
- `--build-arg` support for image builds
- Services/sidecars: `ctx.StartService()`, `ctx.WaitForHealthy()`
- Real-time log streaming from nested containers
- Exception propagation with nesting depth tracking
- Integration tests for all examples (parallelized with NUnit)

### Remaining for v0.1
- DAG-based parallel execution (currently sequential)
- Artifacts: `ctx.Output()`, `ctx.Input()`
- `.Needs()` dependency declaration

---

## Remaining Implementation

### DAG-Based Parallel Execution

**Goal:** Run independent steps concurrently while respecting dependencies.

Currently steps run sequentially. Need to:
1. Add `.Needs()` to `StepBuilder` for explicit dependencies
2. Build dependency graph from step declarations
3. Execute ready steps in parallel, track completion
4. Add concurrency limit option

**Execution algorithm:**

```
1. Build dependency graph from step declarations
2. Identify steps with no dependencies (ready set)
3. While steps remain:
   a. Run all ready steps in parallel
   b. Wait for any to complete
   c. Mark completed, check if dependents are now ready
   d. Add newly ready steps to ready set
4. Collect results, report failures
```

### Artifact Store

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

## Future Phases (post v0.1)

### v0.2: Dynamic DAGs
- `Plan()` function for runtime step generation
- `IPlanContext` for planning phase
- Conditional step creation based on file changes

### v0.3: Compiled Pipelines
- Assembly-based pipeline format
- `[Pipeline]` attribute
- Unit testing pipelines directly

### v0.4: Server Mode
- Central coordinator
- Web UI
- Webhooks for triggers
- Build history

---

## Project Structure (Current)

```
ilmarinen/
├── src/
│   ├── Ilmarinen.Core/           # Domain models, interfaces
│   │   ├── Models/
│   │   │   ├── Step.cs           # Step<T>, StepBuilder<T>
│   │   │   └── ImageRef.cs
│   │   └── Execution/
│   │       ├── IJobContext.cs
│   │       └── Exceptions.cs
│   │
│   ├── Ilmarinen.Scripting/      # Roslyn script engine
│   │   ├── PipelineScript.cs
│   │   └── ScriptGlobals.cs
│   │
│   ├── Ilmarinen.Docker/         # Docker execution engine
│   │   ├── PipelineRunner.cs     # Orchestration + embedded shell script
│   │   ├── DockerJobContext.cs
│   │   └── AgentApiServer.cs     # HTTP API for agent CLI
│   │
│   └── Ilmarinen.Cli/            # Main CLI
│       └── Program.cs
│
├── tests/
│   └── Ilmarinen.Core.Tests/     # NUnit tests (parallelized)
│
├── examples/                      # Example pipelines
│   ├── hello.csx
│   ├── nested.csx
│   ├── services.csx
│   ├── step-output.csx
│   └── cli-injection.csx
│
└── Ilmarinen.sln
```

---

## Dependencies

### NuGet packages
- `Microsoft.CodeAnalysis.CSharp.Scripting` - Roslyn scripting
- `Docker.DotNet` - Docker API client
- `Spectre.Console` - Pretty console output

### External
- Docker daemon
- .NET 8.0 SDK
