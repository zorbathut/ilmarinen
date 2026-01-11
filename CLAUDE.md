# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ilmarinen is a container-native CI/CD system where pipelines are defined in C# (.csx scripts) rather than YAML. Everything runs in containers, and the system uses DAGs (Directed Acyclic Graphs) as first-class constructs.

## Build and Development Commands

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run specific test by name
dotnet test --filter "TestClass.TestMethod"

# Format code
dotnet format

# Run the CLI with a pipeline
dotnet run --project src/Ilmarinen.Cli/Ilmarinen.Cli.csproj -- examples/hello.csx

# Enable debug mode (full stack traces)
DEBUG=1 dotnet run --project src/Ilmarinen.Cli/Ilmarinen.Cli.csproj -- examples/hello.csx
```

## Architecture

### Project Structure

- **Ilmarinen.Core** - Domain models (`Step<T>`, `ImageRef`) and interfaces (`IJobContext`)
- **Ilmarinen.Scripting** - Roslyn-based .csx script loading and compilation
- **Ilmarinen.Docker** - Docker execution engine, pipeline orchestration, agent API server
- **Ilmarinen.Cli** - Main entry point

### Key Design Pattern

Steps are defined with a fluent builder pattern. The lambda in `Run()` executes on the **host**, not in the container. Commands are dispatched to the container via `IJobContext`:

```csharp
Step("build")
    .Image("dotnet/sdk:8.0")
    .Run(async ctx => {
        // This runs on HOST - ctx.Exec() dispatches to container
        await ctx.Exec("dotnet", "build");
    });
```

### Execution Flow

1. CLI loads `.csx` file
2. Roslyn compiles script against `ScriptGlobals` (provides `Step()` function)
3. Steps are collected into `List<Step<object?>>`
4. `PipelineRunner` orchestrates execution:
   - Starts `AgentApiServer` (HTTP on random port with bearer token)
   - Creates Docker network
   - For each step: pulls image, creates container, executes action
5. Containers communicate back via HTTP API for nested operations (`run`, `build`, `secret`, `service`)

### Agent Communication

Each container receives:
- `/workspace` bind mount (shared across all steps)
- `/usr/local/bin/ilmarinen` shell script for CLI commands
- `ILMARINEN_API` and `ILMARINEN_TOKEN` environment variables

### Type System

- `Step<T>` - Generic step that returns value of type T
- `Step<ImageRef>` - Special case for steps that build container images
- `ImageResolver` - Defers image resolution to runtime for dependency chains

### Exception Hierarchy

- `CommandException` - Command failed with exit code and output
- `ShellException` - Shell script failed (extends CommandException)
- `NestedContainerException` - Nested container failed (tracks nesting depth)

## Key Files

- `src/Ilmarinen.Core/Models/Step.cs` - Step model and fluent builders
- `src/Ilmarinen.Docker/PipelineRunner.cs` - Main orchestration logic
- `src/Ilmarinen.Docker/DockerJobContext.cs` - Docker IJobContext implementation
- `src/Ilmarinen.Docker/AgentApiServer.cs` - HTTP API for agent communication
- `src/Ilmarinen.Scripting/PipelineScript.cs` - Roslyn script loading
