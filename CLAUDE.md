# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Ilmarinen is a container-native CI/CD system where pipelines are defined in C# (.csx scripts) rather than YAML. Everything runs in containers, and the system uses DAGs (Directed Acyclic Graphs) as first-class constructs. It supports both local execution (CLI mode) and distributed execution (server/worker mode).

## Interaction Guidelines

**Answer questions before coding**: When asked a question, provide an actual answer first. Don't leap straight to writing code.

**Evaluate, don't assume**: "Why don't we X?" is a request for evaluation, not a suggestion to do X. Explain the tradeoffs, potential issues, or reasons why X might or might not be a good idea.

**Debug by evidence, not by guess**: When investigating a bug you don't fully understand, prefer adding diagnostic instrumentation or asking focused questions over making speculative changes. A confident theory backed by reading the code is fine to act on; a vibe is not. If a fix doesn't solve the user's problem, that's a signal that the theory was wrong — gather more data before trying again. Two consecutive failed fixes mean stop guessing entirely: pause, instrument, and ask. Rapid-fire blind changes waste the user's attention and erode trust.

**Err on the side of more diagnostic data, not less**: When you ask the user to run something — a probe build, a manual test, a copy-paste session — the expensive part is the round trip itself. The marginal cost of one more printed value, one more covered code path, one more chapter to click is small. So when you instrument, instrument generously: log every variable that could plausibly disambiguate the bug, exercise every endpoint of the parameter space (V=0, V=0.5, V=1, not just whichever was easy), include both the suspected-correct prediction *and* the alternatives so residuals are immediately visible. A diagnostic that prints 30 lines and answers the question on the first try is far cheaper than three diagnostics that each print 3 lines. Make the round trip pay for itself.

## Workflow

**Step 1 — Plan.** Enter plan mode (the actual `EnterPlanMode` tool — not a freeform text plan) and research the task and produce a plan. Skippable for trivial changes (under ~a dozen lines). Include unit tests in the plan whenever they're plausible to add — UI generally can't be tested, most other things can.

**Step 2 — Hostile-review the plan.** Before leaving plan mode, spawn a hostile-review agent against the plan itself. Brief it like a design reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), give it the plan, but do not justify the plan's choices. Give it enough feedback space to actually push back on the approach. Apply the same adjudication rules as the final review (below). Fold valid objections into the plan, then exit plan mode.

**Step 3 — Tests first (when applicable).** For bugfixes, or any feature whose tests can be sensibly written before the implementation exists, write the tests first and verify they fail. Then complete the implementation.

**Step 4 — Run all tests.** Always, even when the change seems unrelated. If anything breaks, return to step 3 — or step 1 if the fix requires significant redesign. For UI changes that can't be unit-tested, explicitly say so rather than claiming success.

Don't treat a failing test as a hard veto on the change. Tests exist to catch *unintentional* drift — a test that pins behavior the change deliberately replaced should be updated alongside the code, not worked around to preserve the old behavior. Fix the test to match the new intent; only fall back to step 3 / step 1 when the failure exposes an actual regression.

**Step 5 — Hostile review.** Spawn a hostile-review agent. Brief it like a PR reviewer: explain the problem being solved, point it at CLAUDE.md (and the rest of the tree — it can read whatever it needs to research), but do not explain or justify the implementation. Explicitly ask it to **review the general architecture** too, not just the diff — does the chosen approach fit the surrounding code, are there cleaner factorings, does it introduce abstractions that don't pay rent, etc. Give it enough feedback space to cover both the local change and the architectural read effectively (don't cap it to a terse response). Then:
  - If it raises valid objections, fix them. Significant redesign → back to step 1; code changes → back to step 3.
  - If I disagree with an objection, push back once. If it still objects and I'm still confident, surface the disagreement to the user for adjudication rather than looping.
  - Either way — adjudication needed or not — give the user a quick summary of the review at the end.

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

## Coding Guidelines

**KISS / YAGNI / MVP**: Keep it simple. Write the simplest code that solves the current problem. Include what's necessary, not more. Don't build abstractions, features, or speculative generality that aren't immediately needed. Three similar lines is better than a premature abstraction.

**No backwards compatibility for its own sake**: Remove stubs and dead code completely. If something is unused or being replaced, delete it outright — don't leave shims, renamed `_unused` vars, `// removed` comments, or compatibility re-exports behind. The git history is the backwards compatibility.

**Avoid default parameters**: Prefer explicit overloads or requiring all parameters at call sites. Default parameters hide complexity and make call sites harder to understand.

**Error handling**:
- Don't add excessive or preemptive error handling. Don't validate everything before it's ever been an issue. Trust internal code and framework guarantees; only validate at system boundaries (user input, external APIs).
- **Silent error handling is banned.** Never swallow exceptions or ignore error conditions. If something fails, it must be reported (via the project's logging facility) or thrown. An empty `catch` is a bug.
- For services that face users, distinguish bugs from user mistakes in your status codes / error types. A user submitting bad input should get a specific, helpful error — not a generic 500-equivalent. Reserve "internal error" responses for actual bugs and infrastructure failures. (See Architecture → Error Handling below for the project-specific HTTP status code policy.)

**C# usings**: Implicit usings are disabled in this project. All `using` directives must be explicit and alphabetized at the top of each file.

**Don't unnecessarily remove comments**: Existing comments are there for a reason. If a comment is out of date or actively misleading, fix or remove it. Otherwise leave it alone — don't strip comments just because they explain the "what" rather than the "why", or because you wouldn't have written them yourself.

**Default to writing no comments yourself**. Only add one when the *why* is non-obvious: a hidden constraint, a subtle invariant, a workaround for a specific bug, behavior that would surprise a reader. Don't reference the current task, fix, or callers ("used by X", "added for the Y flow", "handles the case from issue #123") — those belong in the commit message and rot as the codebase evolves.

**Don't hand-wrap lines**: Don't manually break comments or code onto multiple lines to fit a character limit. Good editors handle soft-wrapping. Let lines be as long as they naturally want to be; only break when it genuinely improves readability (a paragraph split, or a structurally-motivated break in a long expression).

**Composition over inheritance**: Prefer building behavior out of small composable pieces (functions, components, properties, modules) over deep class hierarchies. Inheritance is a tool, not a default.

**Data-driven where it pays**: When a category of behavior is open-ended (content, configuration, content variants), prefer data files and a small interpreter over hardcoded code paths. When it's closed and unlikely to grow, just write the code.

## Naming

**Category-instance prefix**: When a name combines a category with an instance, put the category first so related names group alphabetically and the category reads as the classification. `SpawnerBurst`, `ShapeRadial`, `AttackStart()` — not `BurstSpawner`, `RadialShape`, `StartAttack()`. The category is the "kind of thing"; the instance is the specific variant. Apply this to types, functions, files, and config keys alike.

## Critical Rules

1. **Always use absolute paths** in file operations. Relative paths break under tooling that runs from a different working directory than expected.
2. **Tests live next to the code they test** in spirit even if not in directory layout. New code without tests is a debt that compounds.

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

### Testing Philosophy

Run the full test suite on every change, not just the tests you think are related — that's the whole point of having a suite. If the project ships with a watch mode or a fast subset, prefer that during the inner loop, but the final pre-commit step is the full run.

Write tests against the *seam* you actually want to defend — pure functions, deterministic state machines, parsers, classifiers — and don't try to retrofit unit tests around UI, rendering, or process-orchestration code that has no testable seam. For those, say so explicitly when reporting status, and rely on a manual smoke instead of pretending coverage you don't have.

Integration tests that spin up real dependencies (databases, message brokers, container runtimes) are usually worth the slowness over mocks: mocks pass when the contract drifts, real dependencies fail loudly. When mocking is unavoidable, mock at the *outermost* boundary you reasonably can.

## Executing Actions with Care

Carefully consider the reversibility and blast radius of actions. Local, reversible actions (editing files, running tests, running ephemeral scripts) are fine to take freely. But for actions that are hard to reverse, affect shared systems beyond the local environment, or touch production, **confirm first**.

Examples that warrant confirmation:
- Destructive: deleting files/branches, dropping tables, killing processes, `rm -rf`, overwriting uncommitted changes.
- Hard to reverse: force-push, `git reset --hard`, amending published commits, dependency downgrades, CI/CD changes.
- Visible to others: pushing code, creating/closing PRs or issues, sending messages, posting to external services.
- Uploading content to third-party tools (pastebins, diagram renderers): may be cached or indexed even if later deleted.

When you encounter an obstacle, do not use destructive actions as a shortcut to make it go away. Identify root causes; don't bypass safety checks (`--no-verify`, `--no-gpg-sign`) unless the user has explicitly asked. If you discover unexpected state — unfamiliar files, branches, locks — investigate before deleting; it may be the user's in-progress work.

## Tone for Updates

Match response length to the task. A simple question gets a direct answer, not headers and sections. End-of-turn summaries should be one or two sentences — what changed and what's next. Don't narrate internal deliberation; state results and decisions directly. Brief is good; silent is not.

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
