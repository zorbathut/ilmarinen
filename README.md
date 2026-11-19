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

## Notifications

Ilmarinen includes a notification framework that pushes job completion events to external subscribers. The first built-in subscriber is a Discord bot.

### Discord Bot

The Discord bot posts build results (success/failure) to a Discord channel as rich embeds showing repository, branch, duration, and pipeline info.

It also watches for stuck jobs: queued jobs that no available worker can take, either because no worker is available or because none has the minimum worker priority a job requires. A worker is available when it is connected and either ready for work or already running a job, so a worker busy with a long job still counts. Once jobs have been stuck for 5 minutes, the bot posts an alert listing them, and it posts again when none are.

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

`estimatedDrainSeconds` is how long until everything running or queued now has finished, simulating dispatch over the available workers (connected, and ready or running a job) with each job's duration estimated from that worker's past runs of the same script, or other workers' when it has too few. It is optimistic: jobs with no estimate either way (`unestimatedJobs`) count as no time, a worker is taken to be free the moment its job ends, and queued jobs no available worker qualifies for (`unsatisfiableJobs`) are left out — those are the ones `GET /api/jobs/unsatisfiable` lists. Each request runs a handful of queries however long the queue is, but they read job history, so poll every 15–30 seconds rather than every second:

```bash
curl -s "$SERVER/api/jobs/queue/estimate" | jq -r '"\(.queuedJobs) queued · ~\((.estimatedDrainSeconds / 60) | ceil)m"'
```

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
