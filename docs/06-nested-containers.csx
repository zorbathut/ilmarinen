// Nested Container Orchestration
//
// Demonstrates how build scripts can dynamically build and run containers
// using the `ilmarinen` CLI. This enables:
//
// - Python/shell scripts that make runtime decisions about what to build/run
// - Dynamic service spawning based on test configuration
// - Nested containers with full access to secrets, artifacts, and workspace
//
// IMPORTANT: The `ilmarinen` CLI is automatically injected into ANY container.
// No special base images required - use python:3.11, node:20, golang:1.21, etc.
// The CLI binary is mounted at /usr/local/bin/ilmarinen at runtime.
// For minimal containers (scratch/distroless), use the HTTP API at $ILMARINEN_API.
//
// ============================================================================
// CLI QUICK REFERENCE
// ============================================================================
//
// Build images:
//   ilmarinen build -f Dockerfile -t myimage:latest
//   ilmarinen build -f Dockerfile -t myimage --build-arg VERSION=1.0
//
// Run containers (blocks until complete):
//   ilmarinen run myimage -- ./command.sh
//   ilmarinen run myimage --env FOO=bar -- ./script.sh
//   OUTPUT=$(ilmarinen run myimage -- cat /result.txt)
//
// Dynamic services:
//   ilmarinen service start postgres:15 --name db --port 5432
//   ilmarinen service wait db --health-url tcp://db:5432
//   ilmarinen service stop db
//   ilmarinen service list
//
// Access Conductor context:
//   API_KEY=$(ilmarinen secret get api-key)
//   ilmarinen artifact output ./results --name test-results
//   ilmarinen artifact input upstream-data --dest ./data
//   ilmarinen info branch
//
// ============================================================================

#r "Conductor"

// ============================================================================
// EXAMPLE 1: Simple Build-Then-Run
// ============================================================================
// Build a test container, then run it against the application.
// This pattern is useful when your test harness itself needs to be containerized.

var simpleExample = Step("build-then-run")
    .Image("docker:24-cli")  // Any image works - ilmarinen CLI auto-injected
    .Run(async ctx =>
    {
        // Build the application
        await ctx.Shell(@"
            ilmarinen build -f Dockerfile -t myapp:latest
        ");

        // Build a custom test harness
        await ctx.Shell(@"
            ilmarinen build -f tests/Dockerfile -t test-harness:latest \
                --build-arg APP_VERSION=$(ilmarinen info commit)
        ");

        // Run tests in the harness against the app
        await ctx.Shell(@"
            # Start the app as a service
            ilmarinen service start myapp:latest --name app --port 8080
            ilmarinen service wait app --health-url http://app:8080/health

            # Run the test harness
            ilmarinen run test-harness:latest -- pytest /tests -v

            # Cleanup
            ilmarinen service stop app
        ");
    });


// ============================================================================
// EXAMPLE 2: Python Orchestration Script
// ============================================================================
// A Python script that dynamically decides what to build and test based on
// configuration. The script has full control over container orchestration.

var pythonOrchestration = Step("python-orchestration")
    .Image("python:3.11")  // Standard Python image - ilmarinen CLI auto-injected
    .Run(async ctx =>
    {
        // Write the orchestration script
        await ctx.WriteText("orchestrate.py", @"
#!/usr/bin/env python3
import subprocess
import json
import sys

def run(cmd):
    '''Run a command and return (exit_code, stdout)'''
    result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    return result.returncode, result.stdout.strip()

def ilmarinen(args):
    '''Run ilmarinen CLI command'''
    code, output = run(f'ilmarinen {args}')
    if code != 0:
        print(f'ilmarinen {args} failed with code {code}', file=sys.stderr)
        sys.exit(code)
    return output

# Load test configuration
with open('test-matrix.json') as f:
    config = json.load(f)

# Get current branch for conditional logic
branch = ilmarinen('info branch')
print(f'Running on branch: {branch}')

# Build base image once
print('Building base image...')
ilmarinen('build -f Dockerfile -t app:base')

# Build and test each variant
results = {}
for variant in config['variants']:
    name = variant['name']
    print(f'\n=== Testing variant: {name} ===')

    # Build variant-specific image
    build_args = ' '.join(f'--build-arg {k}={v}' for k, v in variant.get('build_args', {}).items())
    ilmarinen(f'build -f Dockerfile.{name} -t app:{name} {build_args}')

    # Run tests for this variant
    exit_code, output = run(f'ilmarinen run app:{name} -- ./run-tests.sh')
    results[name] = {'success': exit_code == 0, 'output': output}

    if exit_code != 0:
        print(f'Variant {name} failed!')
        if branch == 'main':
            # On main branch, fail fast
            sys.exit(1)
        # On feature branches, continue testing other variants

# Output results as artifact
with open('test-results.json', 'w') as f:
    json.dump(results, f, indent=2)
ilmarinen('artifact output ./test-results.json --name variant-results')

# Summary
failed = [k for k, v in results.items() if not v['success']]
if failed:
    print(f'Failed variants: {failed}')
    sys.exit(1)
print('All variants passed!')
");

        // Create sample test matrix
        await ctx.WriteJson("test-matrix.json", new
        {
            variants = new[]
            {
                new { name = "alpine", build_args = new { BASE = "alpine:3.19" } },
                new { name = "debian", build_args = new { BASE = "debian:bookworm-slim" } },
                new { name = "ubuntu", build_args = new { BASE = "ubuntu:22.04" } }
            }
        });

        // Run the orchestration
        await ctx.Exec("python", "orchestrate.py");
    });


// ============================================================================
// EXAMPLE 3: Dynamic Integration Test Environment
// ============================================================================
// Spawn services dynamically based on test requirements discovered at runtime.

var dynamicIntegration = Step("dynamic-integration")
    .Image("docker:24-cli")
    .Run(async ctx =>
    {
        await ctx.Shell(@"
#!/bin/bash
set -e

# Discover what services our tests need by scanning test files
SERVICES=$(grep -rh '@requires_service' tests/ | sed 's/.*@requires_service(\(.*\))/\1/' | sort -u)

echo ""Discovered required services: $SERVICES""

# Start each required service
for svc in $SERVICES; do
    case $svc in
        postgres)
            ilmarinen service start postgres:15 --name postgres --port 5432 \
                --env POSTGRES_PASSWORD=test \
                --env POSTGRES_DB=testdb
            ilmarinen service wait postgres --health-url tcp://postgres:5432
            ;;
        redis)
            ilmarinen service start redis:7 --name redis --port 6379
            ilmarinen service wait redis --health-url tcp://redis:6379
            ;;
        elasticsearch)
            ilmarinen service start elasticsearch:8.11.0 --name elasticsearch --port 9200 \
                --env ""discovery.type=single-node"" \
                --env ""xpack.security.enabled=false""
            ilmarinen service wait elasticsearch --health-url http://elasticsearch:9200/_cluster/health
            ;;
        rabbitmq)
            ilmarinen service start rabbitmq:3-management --name rabbitmq --port 5672
            ilmarinen service wait rabbitmq --health-url tcp://rabbitmq:5672
            ;;
        *)
            echo ""Unknown service: $svc""
            exit 1
            ;;
    esac
    echo ""Started $svc""
done

# Show what's running
ilmarinen service list

# Run the integration tests
dotnet test --filter Category=Integration

# Cleanup (services are also auto-cleaned when step ends)
for svc in $SERVICES; do
    ilmarinen service stop $svc || true
done
");
    });


// ============================================================================
// EXAMPLE 4: Nested Container with Conductor Context
// ============================================================================
// A nested container that itself needs to access secrets, artifacts, and
// other Conductor features. The ilmarinen CLI works inside nested containers.

var nestedContext = Step("nested-with-context")
    .Image("docker:24-cli")
    .Run(async ctx =>
    {
        // Create a script that will run inside the nested container
        await ctx.WriteText("nested-script.sh", @"
#!/bin/bash
set -e

echo ""Running inside nested container...""
echo ""I can access Conductor context:""

# Access secrets
DB_PASSWORD=$(ilmarinen secret get db-password)
echo ""Got database password (${#DB_PASSWORD} chars)""

# Access build metadata
echo ""Branch: $(ilmarinen info branch)""
echo ""Commit: $(ilmarinen info commit)""
echo ""Build URL: $(ilmarinen info build-url)""

# Pull artifacts from previous steps
ilmarinen artifact input build-output --dest ./artifacts
ls -la ./artifacts/

# Do some work with the artifacts
echo ""Processing artifacts...""
# ... processing logic here ...

# Output new artifacts
mkdir -p results
echo ""processed"" > results/status.txt
ilmarinen artifact output ./results --name processed-output

echo ""Nested container complete!""
");

        // Build a container that has our script
        await ctx.Shell(@"
            # Create a simple Dockerfile for the nested container
            cat > Dockerfile.nested << 'DOCKERFILE'
FROM docker:24-cli
COPY nested-script.sh /nested-script.sh
RUN chmod +x /nested-script.sh
CMD [""/nested-script.sh""]
DOCKERFILE

            # Build it
            ilmarinen build -f Dockerfile.nested -t nested-worker:latest

            # Run it - the nested container has full ilmarinen CLI access
            ilmarinen run nested-worker:latest
        ");

        // The artifacts output by the nested container are available here
        ctx.Input("processed-output", "processed/");
    });


// ============================================================================
// EXAMPLE 5: Parallel Container Fan-Out
// ============================================================================
// Spawn multiple containers in parallel for load testing or parallel processing.

var parallelFanOut = Step("parallel-fan-out")
    .Image("docker:24-cli")
    .Run(async ctx =>
    {
        await ctx.Shell(@"
#!/bin/bash
set -e

# Build the worker image
ilmarinen build -f worker/Dockerfile -t worker:latest

# Configuration
WORKER_COUNT=5
WORK_ITEMS=$(seq 1 20)

# Start workers as services (they'll pull work from a queue)
ilmarinen service start redis:7 --name queue --port 6379
ilmarinen service wait queue --health-url tcp://queue:6379

# Populate the work queue
for item in $WORK_ITEMS; do
    redis-cli -h queue LPUSH work ""job:$item""
done
echo ""Queued $(echo $WORK_ITEMS | wc -w) work items""

# Start worker services
for i in $(seq 1 $WORKER_COUNT); do
    ilmarinen service start worker:latest --name worker-$i \
        --env REDIS_URL=redis://queue:6379 \
        --env WORKER_ID=$i
done
echo ""Started $WORKER_COUNT workers""

# Wait for queue to drain (poll until empty)
while true; do
    REMAINING=$(redis-cli -h queue LLEN work)
    echo ""Remaining work items: $REMAINING""
    if [ ""$REMAINING"" = ""0"" ]; then
        break
    fi
    sleep 5
done

# Collect results from workers
mkdir -p results
for i in $(seq 1 $WORKER_COUNT); do
    # Each worker writes results to a shared volume
    cp -r /workspace/worker-$i-results/* results/ 2>/dev/null || true
done

ilmarinen artifact output ./results --name parallel-results

# Cleanup
for i in $(seq 1 $WORKER_COUNT); do
    ilmarinen service stop worker-$i
done
ilmarinen service stop queue
");
    });


// ============================================================================
// EXAMPLE 6: Meta-CI - Testing Conductor Pipelines
// ============================================================================
// Use Conductor to test Conductor pipelines. The ilmarinen CLI can run
// sub-pipelines, enabling meta-CI workflows.

var metaCI = Step("meta-ci")
    .Image("docker:24-cli")
    .Run(async ctx =>
    {
        await ctx.Shell(@"
#!/bin/bash
set -e

# We're testing pipeline definitions in the test-pipelines/ directory
for pipeline in test-pipelines/*.csx; do
    echo ""=== Testing pipeline: $pipeline ===""

    # Run the pipeline in a nested Conductor instance
    # --dry-run validates without executing
    # --wait blocks until complete
    if ilmarinen pipeline run ""$pipeline"" --wait --timeout 10m; then
        echo ""PASS: $pipeline""
    else
        echo ""FAIL: $pipeline""
        FAILED=1
    fi
done

if [ ""$FAILED"" = ""1"" ]; then
    echo ""Some pipelines failed!""
    exit 1
fi

echo ""All pipelines passed!""
");
    });


// ============================================================================
// EXAMPLE 7: HTTP API for Minimal Containers
// ============================================================================
// For distroless or scratch containers that can't run the CLI binary,
// use the HTTP API directly. The API is always available at $ILMARINEN_API.

var httpAPIExample = Step("http-api-example")
    .Image("golang:1.21")  // Standard Go image
    .Run(async ctx =>
    {
        // Write a Go program that uses the HTTP API directly
        await ctx.WriteText("main.go", @"
package main

import (
    ""bytes""
    ""encoding/json""
    ""fmt""
    ""io""
    ""net/http""
    ""os""
)

func main() {
    api := os.Getenv(""ILMARINEN_API"")
    if api == """" {
        api = ""http://ilmarinen.local:8080""
    }

    // Get a secret via HTTP API
    resp, err := http.Get(api + ""/api/secret/db-password"")
    if err != nil {
        panic(err)
    }
    defer resp.Body.Close()
    secret, _ := io.ReadAll(resp.Body)
    fmt.Printf(""Got secret: %d bytes\n"", len(secret))

    // Get build info
    resp, _ = http.Get(api + ""/api/info/branch"")
    branch, _ := io.ReadAll(resp.Body)
    fmt.Printf(""Branch: %s\n"", branch)

    // Start a service via HTTP API
    serviceReq := map[string]interface{}{
        ""image"": ""redis:7"",
        ""name"":  ""cache"",
        ""ports"": []int{6379},
    }
    reqBody, _ := json.Marshal(serviceReq)
    resp, _ = http.Post(api+""/api/service/start"", ""application/json"",
        bytes.NewReader(reqBody))
    fmt.Println(""Started redis service"")

    // ... do work with the service ...

    // Stop the service
    stopReq := map[string]string{""name"": ""cache""}
    reqBody, _ = json.Marshal(stopReq)
    http.Post(api+""/api/service/stop"", ""application/json"",
        bytes.NewReader(reqBody))
}
");

        // Note: This pattern is useful when building distroless Go binaries
        // that need to orchestrate containers without a shell
        await ctx.Shell(@"
            echo 'This example shows HTTP API usage for Go.'
            echo 'For distroless containers, compile this and include in your image.'
            echo 'The HTTP API at $ILMARINEN_API works from any language.'
        ");
    });
