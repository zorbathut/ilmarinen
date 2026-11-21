# Ilmarinen

[![Language: C#](https://img.shields.io/badge/language-C%23-blue)](https://docs.microsoft.com/en-us/dotnet/csharp/) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE) [![Build status](https://img.shields.io/github/actions/workflow/status/zorbathut/ilmarinen/test.yml?branch=dev)](https://github.com/zorbathut/ilmarinen/actions?query=workflow%3Atest+branch%3Adev)

A container-native CI/CD system where pipelines are defined in C#.

## Features

- **C# Pipeline Definitions** - Write pipelines as `.csx` scripts with full IDE support, type safety, and the entire .NET ecosystem
- **Container-Native** - Every step runs in an isolated container; no ambient environment pollution
- **Nested Containers** - Steps can spawn additional containers, build images, and run services
- **Step Dependencies** - Pass outputs between steps (e.g., build an image in one step, use it in the next)
- **Service Containers** - Spin up databases, caches, or other services for integration tests
- **Local And Distributed Execution** - Run locally or submit jobs to a central server with worker pools

## Quick Start

### Local Execution

```bash
# Run a pipeline locally
dotnet run --project src/Ilmarinen.Cli -- examples/hello.ilmarinen.csx
```

### Server Mode

The infrastructure is split into layered Docker Compose files. The `.env` file controls which services start by default:

```bash
# Start server + worker (default, configured in .env)
docker compose up -d

# Start server only
docker compose -f docker-compose.yml up -d

# Start server + worker + Discord bot
docker compose -f docker-compose.yml -f docker-compose.worker.yml -f docker-compose.discord.yml up -d
```

To permanently add the Discord bot, edit `.env`:
```
COMPOSE_FILE=docker-compose.yml:docker-compose.worker.yml:docker-compose.discord.yml
```

The compose files above are the **dev stack**: one host, built from source, keys in a gitignored override. They are not a deployment. The official deployments each live in their own directory and are configured with a `.env`:

| | |
|---|---|
| [`deploy/server-docker/`](deploy/server-docker/) | Server + PostgreSQL, on any Docker host. **Read its security section — Ilmarinen has no authentication**, so it binds to loopback by default. |
| [`deploy/worker-docker/`](deploy/worker-docker/) | Worker on any Docker host. |
| [`deploy/worker-nix/`](deploy/worker-nix/) | Worker on NixOS, via systemd. |

The Discord bot has no deployment of its own yet — `docker-compose.discord.yml` is a dev-stack overlay, since it reaches the server over the stack's internal network.

```bash
# Submit a job
dotnet run --project src/Ilmarinen.Cli -- submit \
  --server http://localhost:1551 \
  --repo https://github.com/your/repo.git \
  --script pipeline.csx

# Check job status
dotnet run --project src/Ilmarinen.Cli -- status \
  --server http://localhost:1551 \
  <job-id>
```

## Pipeline Examples

### Hello World

```csharp
// hello.csx
Step("hello")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        await ctx.Exec("echo", "Hello from ilmarinen!");
        await ctx.Shell("echo 'Current directory:' && pwd && ls -la");
    });
```

### Build and Use an Image

```csharp
// Build an image in one step, use it in the next
var build = Step<ImageRef>("build")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        await ctx.Shell("cat > /workspace/Dockerfile << 'EOF'\nFROM alpine\nRUN echo 'hello' > /message.txt\nEOF");
        return await ctx.BuildImage("Dockerfile", "my-app:latest");
    });

Step("run")
    .Image(() => build.Output!)  // Lazy reference resolved at runtime
    .Run(async ctx =>
    {
        await ctx.Exec("cat", "/message.txt");
    });
```

### Service Containers

```csharp
Step("integration-test")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        // Start Redis as a background service
        var redis = await ctx.StartService("redis:alpine", "redis", [6379]);

        try
        {
            await ctx.WaitForHealthy("tcp://redis:6379", TimeSpan.FromSeconds(30));
            await ctx.Run("redis:alpine", "redis-cli", "-h", "redis", "PING");
        }
        finally
        {
            await redis.StopAsync();
        }
    });
```

### Nested Containers

```csharp
Step("nested")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        // Run a command in a nested container
        await ctx.Run("alpine:latest", "echo", "Hello from nested container!");

        // Nested containers share the /workspace volume
        await ctx.Shell("echo 'data' > /workspace/file.txt");
        await ctx.Run("alpine:latest", "cat", "/workspace/file.txt");
    });
```

## Architecture

### Components

| Component | Description |
|-----------|-------------|
| **Ilmarinen.Cli** | Command-line interface for local execution and job submission |
| **Ilmarinen.Server** | Web server with job queue, worker coordination, and dashboard |
| **Ilmarinen.Worker** | Executes jobs by cloning repos and running pipelines |
| **Ilmarinen.Core** | Domain models (`Step<T>`, `ImageRef`, `IJobContext`) |
| **Ilmarinen.Docker** | Docker execution engine and pipeline orchestration |
| **Ilmarinen.Scripting** | Roslyn-based .csx script compilation |
| **Ilmarinen.Protocol** | Shared types for server/worker communication |
| **Ilmarinen.Database** | PostgreSQL persistence with Entity Framework Core |
| **Ilmarinen.NotificationClient** | Shared library for subscribing to job notifications |
| **Ilmarinen.DiscordBot** | Discord bot that reports build successes and failures, and jobs stuck waiting for a worker |

### Execution Model

The key insight is that the lambda in `Run()` executes on the **host** (or worker), not inside the container. The `IJobContext` provides methods to dispatch commands to the container:

```csharp
Step("example")
    .Image("alpine:latest")
    .Run(async ctx =>
    {
        // This C# code runs on the host
        var result = await ctx.Exec("ls", "-la");  // This runs in the container

        if (result.ExitCode == 0)
        {
            // Back on the host, making decisions based on container output
            await ctx.Shell("echo 'success'");
        }
    });
```

### IJobContext API

| Method | Description |
|--------|-------------|
| `Exec(cmd, args...)` | Execute a command in the container |
| `Shell(script)` | Execute a shell script in the container |
| `Run(image, cmd, args...)` | Run a nested container |
| `BuildImage(dockerfile, tag)` | Build a Docker image |
| `StartService(image, name, ports)` | Start a background service container |
| `WaitForHealthy(url, timeout)` | Wait for a TCP/HTTP endpoint |

### Server Architecture

```
┌─────────────┐     HTTP/SignalR     ┌─────────────┐
│   Client    │ ──────────────────── │   Server    │
│   (CLI)     │                      │             │
└─────────────┘                      │  ┌───────┐  │
                                     │  │ Queue │  │
┌─────────────┐     SignalR          │  └───────┘  │
│   Worker    │ ──────────────────── │             │
│             │                      │  ┌──────┐   │
└─────────────┘                      │  │  DB  │   │
                                     │  └──────┘   │
┌─────────────┐     SignalR          │             │
│   Worker    │ ──────────────────── └─────────────┘
│             │
└─────────────┘
```

- Jobs are submitted via HTTP and stored in PostgreSQL
- Workers connect via SignalR and receive job assignments
- Each worker clones the repo, loads the pipeline script, and executes it
- Job status updates flow back through SignalR

### Priorities

There are two lists of priorities, both the operator's own: worker priorities and job priorities. Each is an ordered list of names, and one entry in each is the default. A fresh install has just "Default" in each; an install upgraded from the fixed Low, Medium and High priorities keeps those that were in use, with Medium the default where it was used. Requests, the CLI and the API name priorities by name, in any case.

A worker has a worker priority (the default when it registers). When several workers are ready, the one with the highest priority takes the next job.

A job has a job priority (the default unless it or its pipeline names one). Whenever a worker is free, it takes the highest-priority queued job it qualifies for, and the oldest among equals. Priority only decides what starts next: a running job is never stopped to make room. A pipeline's priority is what its jobs get unless a trigger overrides it, and `submit` and `trigger` take `--priority`. It is a strict order, so under steady higher-priority load a lower job waits until the queue above it is empty.

A job may also have a minimum worker priority: only a worker of at least that priority runs it. With none, any worker may run it, which is what a job gets unless it or its pipeline names one. `--min-worker-priority any` (or `"any"` over the API) asks for no minimum, overriding a pipeline's.

The Priorities page manages both lists: add, rename, move up and down, choose the default, and delete. Deleting asks for another priority of the same kind and moves every worker, pipeline and job using the deleted one onto it, finished jobs included; the last one of a kind can't be deleted. Changes take effect on the next dispatch. Each entry shows what uses it, linking to the workers, pipelines and jobs narrowed to it. Over the API the same is `/api/priorities/{worker|job}` (list, `POST` to add, `PUT …/order` to reorder), `PUT`, `DELETE ?replacement=` and `POST …/default` on `/api/priorities/{id}`, and `?priority=` (and `?minWorkerPriority=` for pipelines and jobs) on the list endpoints.

### Pausing a Worker

The Pause button on the Workers page (or a worker's own page) stops the server sending that worker new jobs; a job it is already running carries on. It works whether or not the worker is connected, lasts across reconnects and server restarts, and holds until Unpause. The worker itself is never told. A paused worker doesn't count as available, so jobs only it could take are reported as stuck.

### Disk Space

Each worker reports the free space on the filesystem holding its workspaces: when it connects, as each job ends, and every half minute. The Workers page shows it, and a worker's page also shows how much of the space in use is neither workspaces nor git mirrors ("other use"). For a containerized worker whose workspaces are a Docker volume, that includes Docker's images and build cache, which nothing here deletes.

A worker keeps a floor of free space, 5 GB unless `ILMARINEN_MIN_FREE_GB` says otherwise. If a running job takes free space below it, the worker stops the job, which fails with "stopped: free space fell below the worker's floor" on its page, before it can fill the disk. Each job's page shows what it worked in (its persistent workspace, and whether that run made it, or an ephemeral checkout) and how far free space fell while it ran. Anything else writing to the same disk meanwhile counts too.

The server learns how much disk a script's runs take from its latest five runs, and sends a job only to a worker with room for its next one beyond the floor, or able to make room. It takes the most any of those runs took, counting a run stopped for disk double, so a script that keeps running out asks for more each time. Making a persistent workspace afresh is judged apart from reusing one, by the script's latest fresh runs however old. A script with no history needs only the floor. A worker makes room before the job starts, deleting its least recently used persistent workspaces (never the job's own), only as many as their measured sizes say it takes. If reusing the job's own workspace leaves no room but making it afresh would, because it has grown past what a fresh run makes, the worker deletes it and starts over, unless its own reading shows reuse fits after all. Each deletion is written to the job's log.

Among workers of the same priority, a job goes first to one already holding the workspace its script's latest run used, then to one with room without deleting anything, then to the one that deletes least, then to the tightest fit. A higher-priority worker with room still comes first. A job no worker has room for waits, and the stuck-jobs report (and the Discord alert) lists it with what it needs; a later, smaller job may meanwhile take the worker. A need is never taken to be more than the most room any worker could make, by its latest readings, so a run inflated by something else filling the disk can't keep a script off every worker for good: it runs on the roomiest one, and the history moves on. A sleeping worker isn't woken for a job its disk, as last heard, has no room for. The queue estimate doesn't take disk into account.

Known limits: a script's workspace and disk history are keyed by repository and script path, not by branch, so branches whose scripts name different workspaces confuse both. Two scripts whose workspaces don't fit on one worker together keep deleting each other's there. Two workers sharing one filesystem each count its free space as their own.

## Notifications

Ilmarinen includes a notification framework that pushes job completion events to external subscribers. The first built-in subscriber is a Discord bot.

### Discord Bot

The Discord bot posts build results (success/failure) to a Discord channel as rich embeds showing repository, branch, duration, and pipeline info.

It also watches for stuck jobs: queued jobs that no available worker can take, either because no worker is available or because none has the minimum worker priority a job requires (a job with no minimum is stuck only when no worker is available at all). A worker is available when it is connected, not paused, and either ready for work or already running a job, so a worker busy with a long job still counts. Once jobs have been stuck for 5 minutes, the bot posts an alert listing them, naming any paused workers that could take them, and it posts again when none are.

The bot learns this by polling `GET /api/jobs/unsatisfiable` and keeps no record of what it has posted. After a restart it alerts again about jobs that are still stuck, and if the jobs come unstuck while the bot is down, no recovery message is posted.

**Setup:**

1. Create a Discord bot at the [Discord Developer Portal](https://discord.com/developers/applications) and get the bot token
2. Copy the example config and fill in your values:
   ```bash
   cp config/discord-bot.json.example config/discord-bot.json
   ```
   ```json
   {
     "botToken": "your-bot-token",
     "channelId": "your-channel-id",
     "serverUrl": "http://localhost:1551",
     "subscriberName": "discord-bot",
     "mentionRoleId": null
   }
   ```
3. Set `mentionRoleId` to a Discord role ID to ping that role on build failures and stuck-job alerts (optional)

**Running with Docker Compose:**

Add `docker-compose.discord.yml` to your compose stack:

```bash
docker compose -f docker-compose.yml -f docker-compose.worker.yml -f docker-compose.discord.yml up -d
```

Or add it permanently to `.env`:
```
COMPOSE_FILE=docker-compose.yml:docker-compose.override.yml:docker-compose.worker.yml:docker-compose.discord.yml
```

**Running standalone:**

```bash
dotnet run --project src/Ilmarinen.DiscordBot
```

### Custom Notification Subscribers

The notification framework is extensible. To build a custom subscriber (Slack, email, etc.):

1. Reference `Ilmarinen.NotificationClient`
2. Use `IlmarinenNotificationClient` to register, send heartbeats, pull notifications, and acknowledge them
3. Implement `INotificationHandler` for your delivery logic

The server exposes subscriber management via REST:
- `POST /api/subscribers` — register
- `POST /api/subscribers/{id}/heartbeat` — keep-alive
- `POST /api/subscribers/{id}/notifications` — pull pending notifications
- `POST /api/subscribers/{id}/notifications/ack` — acknowledge processed notifications

### Watching the Queue

`GET /api/jobs/queue/estimate` reports how backed up the fleet is, for a status-bar widget or similar:

```json
{ "runningJobs": 2, "queuedJobs": 5, "connectedWorkers": 3, "availableWorkers": 3,
  "estimatedDrainSeconds": 742, "unestimatedJobs": 1, "unsatisfiableJobs": 0 }
```

`estimatedDrainSeconds` is how long until everything running or queued now has finished, simulating dispatch over the available workers (connected, not paused, and ready or running a job) with each job's duration estimated from that worker's past runs of the same script, or other workers' when it has too few. It is optimistic: jobs with no estimate either way (`unestimatedJobs`) count as no time, a worker is taken to be free the moment its job ends, and queued jobs no available worker qualifies for (`unsatisfiableJobs`) are left out — those are the ones `GET /api/jobs/unsatisfiable` lists. Each request runs a handful of queries however long the queue is, but they read job history, so poll every 15–30 seconds rather than every second:

```bash
curl -s "$SERVER/api/jobs/queue/estimate" | jq -r '"\(.queuedJobs) queued · ~\((.estimatedDrainSeconds / 60) | ceil)m"'
```

### Resource Metrics

While a job runs, its worker samples resource use every 2 seconds (`ILMARINEN_METRICS_INTERVAL_SECONDS` on the worker; `0` turns it off). The job page charts CPU, memory and disk over the job, with each step's start marked.

- `GET /api/jobs/{id}/metrics` returns the samples as NDJSON: one versioned record per line (`Ilmarinen.Protocol.Metrics`). The records:
  - **host:** the machine;
  - **container:** each container, the first time it is seen;
  - **step:** each step's start and end, with whether it runs an image an earlier step returned (matched by name, so it can flag a dependency that isn't there, but never misses one);
  - **stepSummary:** what the step used, written when it ends;
  - **pull:** image pulls;
  - **sample:** one per interval.
- A sample holds:
  - whole-host CPU (user, system, iowait, steal), memory (available, free, dirty, swap), disk throughput, ops and busy time, and pressure stall averages;
  - each job container's CPU (and time held back by a CPU limit), working-set memory, disk and network rates, and process count;
  - how many other job networks were on the Docker host at the time: above zero, the host figures include another job's work. A network left behind by a failed cleanup counts too.
- `GET /api/jobs/{id}/steps` summarises each step: duration, status, CPU-seconds, peak cores and memory, disk bytes, and time spent pulling.

The per-container figures cover only the containers on the job's Docker network: the step's own container, nested `Run` containers, services, and the worker's own container (doing checkout and compile). Some work happens outside that network, and shows only in the host figures:
- BuildKit image builds;
- image pulls;
- a script's own `docker run` or `docker compose`.

The host figures describe the machine the worker process runs on. With a remote `DOCKER_HOST`, or Docker inside a VM, that isn't the machine running the containers. Host network traffic isn't recorded.

Memory is sampled, so short spikes can be missed; memory pressure is the signal for those. A container that starts and stops between two samples is missed entirely.

The metrics travel with the job's log as their own versioned records, so a server and workers on different builds still exchange them.

## Development

```bash
# Build
dotnet build

# Test
dotnet test

# Run locally
dotnet run --project src/Ilmarinen.Cli -- examples/hello.ilmarinen.csx

# Enable debug mode (full stack traces)
DEBUG=1 dotnet run --project src/Ilmarinen.Cli -- examples/hello.ilmarinen.csx
```

## Requirements

- .NET 9.0 SDK
- Docker (with API access for the CLI/worker)
- PostgreSQL (for server mode, included in docker-compose)

## License

MIT
