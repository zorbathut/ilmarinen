# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ilmarinen is a container-native CI/CD system where pipelines are defined in C# (.csx scripts) rather than YAML. Everything runs in containers, and the system uses DAGs (Directed Acyclic Graphs) as first-class constructs. It supports both local execution (CLI mode) and distributed execution (server/worker mode).

## Build and Development Commands

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run specific test by name
dotnet test --filter "TestClass.TestMethod"

# Run integration tests only
dotnet test --filter "Category=Integration"

# Format code
dotnet format

# Run the CLI with a pipeline (local execution)
dotnet run --project src/Ilmarinen.Cli/Ilmarinen.Cli.csproj -- examples/hello.ilmarinen.csx

# Enable debug mode (full stack traces)
DEBUG=1 dotnet run --project src/Ilmarinen.Cli/Ilmarinen.Cli.csproj -- examples/hello.ilmarinen.csx

# Run server (distributed mode)
dotnet run --project src/Ilmarinen.Server/Ilmarinen.Server.csproj

# Run worker (connects to server)
dotnet run --project src/Ilmarinen.Worker/Ilmarinen.Worker.csproj
```

## Architecture

### Project Structure

**Core Libraries:**
- **Ilmarinen.Core** - Domain models (`Step<T>`, `ImageRef`) and interfaces (`IJobContext`)
- **Ilmarinen.Scripting** - Roslyn-based .csx script loading and compilation
- **Ilmarinen.Docker** - Docker execution engine, pipeline orchestration, agent API server
- **Ilmarinen.Protocol** - Shared DTOs for server/worker/CLI communication (`JobStatus`, requests/responses)

**Distributed System:**
- **Ilmarinen.Server** - Central orchestration service (ASP.NET Core, job queue, worker coordination via SignalR)
- **Ilmarinen.Worker** - Distributed worker that executes jobs (connects to server, clones repos, runs pipelines)
- **Ilmarinen.Database** - PostgreSQL persistence layer (EF Core entities: `Job`, `Worker`, `JobLogChunk`)

**Entry Points:**
- **Ilmarinen.Cli** - Command-line interface for local execution and job submission (Cocona-based)

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

### Execution Modes

**Local Execution (CLI):**
1. CLI loads `.csx` file via `PipelineScript.LoadAsync()`
2. Roslyn compiles script against `ScriptGlobals` (provides `Step()` function)
3. Steps are collected into `List<Step<object?>>`
4. `PipelineRunner` orchestrates execution:
   - Starts `AgentApiServer` (HTTP on random port with bearer token)
   - Creates Docker network (labeled with PID for cleanup)
   - For each step: pulls image, creates container, executes action
5. Containers communicate back via HTTP API for nested operations (`run`, `build`, `secret`, `service`)

**Distributed Execution (Server/Worker):**
1. CLI submits job via `POST /api/jobs` with `JobSubmission` (RepoUrl, Ref, ScriptPath)
2. Server stores job in PostgreSQL, emits to job queue
3. Worker receives job assignment via SignalR
4. Worker clones repo, loads pipeline script, executes locally
5. Worker streams logs and status updates back via SignalR

### Agent Communication

Each container receives:
- `/workspace` bind mount (shared across all steps)
- `/usr/local/bin/ilmarinen-agent` shell script for CLI commands
- `ILMARINEN_API` and `ILMARINEN_TOKEN` environment variables

The agent HTTP API uses NDJSON streaming format: `{"t":"o"|"e"|"x","d":"...","c":exitCode}`

### IJobContext Interface

The `IJobContext` interface provides these capabilities to step actions:

- `Branch`, `Commit` - Git metadata properties
- `Exec(cmd, args)` / `TryExec(cmd, args)` - Run command in container
- `Shell(script)` / `TryShell(script)` - Execute shell script
- `BuildImage(dockerfile, tag, context, buildArgs)` - Docker build
- `Run(image, command)` / `TryRun(image, command)` - Nested container execution
- `StartService(image, name, ports)` - Background service container
- `WaitForHealthy(url, timeout)` - HTTP/TCP health check
- `Secret(name)` - Retrieve secrets from environment

Streaming variants (`TryExecStreaming`, `TryShellStreaming`, `TryRunStreaming`) provide real-time output.

### Type System

- `Step<T>` - Generic step that returns value of type T
- `Step<ImageRef>` - Special case for steps that build container images
- `ImageResolver` - Defers image resolution to runtime for dependency chains
- Images can be specified as `string`, `Func<ImageRef>`, or `Func<string>` for lazy resolution

### Exception Hierarchy

- `CommandException` - Command failed with exit code, stdout/stderr (truncated)
- `ShellException` - Shell script failed (extends CommandException, preserves original script)
- `NestedContainerException` - Nested container failed (tracks nesting depth, container chain)

### Server/Worker Communication

SignalR hub methods:
- `Register(WorkerRegister)` - Worker joins, validates build compatibility
- `Ready()` - Worker signals availability for jobs
- `StreamLogs(LogChunk)` - Worker streams output chunks
- `JobStarted(jobId)` / `JobCompleted(jobId, result)` - Status updates
- `Heartbeat(WorkerHeartbeat)` - Connection keepalive

### Security

- **Port Isolation**: Server exposes PublicPort (8080) externally, WorkerPort (8081) internally only
- **Protocol Validation**: Server rejects workers with mismatched `ProtocolVersion.Hash`
- **Bearer Token Auth**: AgentApiServer uses random UUID tokens per pipeline run
- **Network Isolation**: Each pipeline run gets unique Docker network

### Error Handling

**Policy: Users should never trigger 500 errors.** HTTP 500 indicates a bug or infrastructure failure, not a user mistake.

Use appropriate status codes:
- **400 Bad Request** - Invalid input, malformed request
- **404 Not Found** - Resource doesn't exist
- **503 Service Unavailable** - Server misconfiguration (missing env vars, etc.)
- **500 Internal Server Error** - Only for true bugs or infrastructure failures (DB down, disk full, data corruption)

Implementation:
- `ConfigurationException` → 503 with helpful message (e.g., missing `ILMARINEN_CREDENTIAL_KEY`)
- `ExceptionHandlerMiddleware` returns ProblemDetails (RFC 7807) for all errors
- Development mode includes full stack traces; production shows "check server logs"

When adding new features, ask: "Can a user trigger this exception through normal API usage?" If yes, return a specific status code with a helpful message.

## Testing

Tests use NUnit framework with `IntegrationTestFixture` for server/worker lifecycle management.

```bash
# Run all tests
dotnet test

# Run specific test
dotnet test --filter "BasicJobFlowTest"

# Run integration tests
dotnet test --filter "Category=Integration"
```

Key test utilities:
- `IntegrationTestFixture` - Manages server/worker lifecycle
- `TestGitRepository` - Creates temporary test repositories
- `/workspace` leak detection in all tests
- Docker network cleanup by PID label

## Key Files

- `src/Ilmarinen.Core/Models/Step.cs` - Step model and fluent builders
- `src/Ilmarinen.Core/Execution/IJobContext.cs` - Job context interface
- `src/Ilmarinen.Docker/PipelineRunner.cs` - Main orchestration logic
- `src/Ilmarinen.Docker/DockerJobContext.cs` - Docker IJobContext implementation
- `src/Ilmarinen.Docker/AgentApiServer.cs` - HTTP API for agent communication
- `src/Ilmarinen.Scripting/PipelineScript.cs` - Roslyn script loading
- `src/Ilmarinen.Server/Hubs/WorkerHub.cs` - SignalR hub for worker communication
- `src/Ilmarinen.Database/Entities/Job.cs` - Job entity model

## Dependencies

- .NET 9.0
- Docker.DotNet - Docker API client
- Roslyn (Microsoft.CodeAnalysis.CSharp.Scripting) - C# script compilation
- ASP.NET Core / SignalR - Web server and real-time communication
- Entity Framework Core with Npgsql - PostgreSQL ORM
- LibGit2Sharp - Git operations
- Cocona - CLI framework
- NUlid - Distributed IDs
- Serilog - Structured logging
- NUnit - Testing framework
