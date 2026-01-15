// Monorepo Pipeline with Dynamic DAG
//
// A complex example showing:
// - Dynamic DAG generation based on repo structure
// - Shared libraries that services depend on
// - Parallel builds with proper dependency ordering
// - Integration tests with built services as sidecars
// - Security scanning in parallel with tests
// - Staged deployment with approvals
//
// Repo structure assumed:
// /
// ├── projects.json           # Manifest describing projects
// ├── libs/
// │   ├── common/
// │   └── auth/
// ├── services/
// │   ├── api-gateway/
// │   ├── user-service/
// │   └── order-service/
// └── k8s/
//     └── overlays/
//         ├── staging/
//         ├── canary/
//         └── production/

#r "Conductor"

// Data models for the manifest
record ProjectManifest(
    Library[] Libraries,
    Service[] Services,
    IntegrationSuite[] IntegrationSuites,
    Dictionary<string, EnvironmentConfig> Environments
);

record Library(string Name, string Path, string? BuildImage);
record Service(string Name, string Path, string? BuildImage, string[] LibraryDependencies);
record IntegrationSuite(string Name, string Path, string[] RequiredServices);
record EnvironmentConfig(string HealthEndpoint);

Plan(async ctx =>
{
    var graph = new Graph();
    var manifest = await ctx.ReadJson<ProjectManifest>("projects.json");

    // Track job nodes for type-safe dependencies
    var libBuilds = new Dictionary<string, JobNode>();
    var svcBuilds = new Dictionary<string, JobNode>();
    var containerized = new Dictionary<string, JobNode>();
    var unitTests = new Dictionary<string, JobNode>();
    var integrationTests = new Dictionary<string, JobNode>();
    var scanJobs = new Dictionary<string, JobNode>();

    // Track built images for use in later jobs
    var builtImages = new Dictionary<string, ImageRef>();
    var testJobs = new List<JobNode>();

    // ========================================
    // SHARED LIBRARIES (must build first)
    // ========================================
    foreach (var lib in manifest.Libraries)
    {
        libBuilds[lib.Name] = graph.Add($"build-{lib.Name}",
            image: lib.BuildImage ?? "mcr.microsoft.com/dotnet/sdk:8.0",
            run: async ctx =>
            {
                await ctx.Exec("dotnet", "build", lib.Path, "-c", "Release");
                await ctx.Exec("dotnet", "pack", lib.Path, "-o", "packages/");
                ctx.Output("packages", "packages/*.nupkg");
            });
    }

    // ========================================
    // SERVICES (depend on their libraries)
    // ========================================
    foreach (var svc in manifest.Services)
    {
        var libDeps = svc.LibraryDependencies.Select(l => libBuilds[l]);

        // Build the service
        svcBuilds[svc.Name] = graph.Add($"build-{svc.Name}",
            image: svc.BuildImage ?? "mcr.microsoft.com/dotnet/sdk:8.0",
            needs: libDeps,
            run: async ctx =>
            {
                ctx.Input("packages");  // Pull in library artifacts
                await ctx.Exec("dotnet", "publish", svc.Path, "-c", "Release", "-o", "out/");
            });

        // Containerize
        containerized[svc.Name] = graph.Add($"containerize-{svc.Name}",
            image: "gcr.io/kaniko-project/executor:latest",
            needs: [svcBuilds[svc.Name]],
            run: async ctx =>
            {
                var tag = $"registry.io/{svc.Name}:${{BUILD_ID}}";
                await ctx.Exec("/kaniko/executor",
                    "--dockerfile", $"{svc.Path}/Dockerfile",
                    "--context", ".",
                    "--destination", tag,
                    "--no-push",
                    "--tar-path", "image.tar");

                return ctx.OutputImage("image.tar", tag);
            },
            capture: img => builtImages[svc.Name] = img);

        // Unit tests (parallel with containerize)
        unitTests[svc.Name] = graph.Add($"test-unit-{svc.Name}",
            image: svc.BuildImage ?? "mcr.microsoft.com/dotnet/sdk:8.0",
            needs: [svcBuilds[svc.Name]],
            run: ctx => ctx.Exec("dotnet", "test", svc.Path,
                "--no-build", "-c", "Release",
                "--filter", "Category!=Integration"));

        testJobs.Add(unitTests[svc.Name]);
    }

    // ========================================
    // INTEGRATION TESTS (need running services)
    // ========================================
    foreach (var suite in manifest.IntegrationSuites)
    {
        var serviceContainerDeps = suite.RequiredServices
            .Select(s => containerized[s]);

        var services = suite.RequiredServices
            .Select(s => new Service(builtImages[s], s, ports: [8080]));

        integrationTests[suite.Name] = graph.Add($"test-integration-{suite.Name}",
            image: "mcr.microsoft.com/dotnet/sdk:8.0",
            needs: serviceContainerDeps,
            services: services,
            run: async ctx =>
            {
                // Wait for all services to be healthy
                foreach (var svc in suite.RequiredServices)
                {
                    await ctx.WaitForHealthy($"http://{svc}:8080/health");
                }

                await ctx.Exec("dotnet", "test", suite.Path,
                    "--filter", "Category=Integration");
            });

        testJobs.Add(integrationTests[suite.Name]);
    }

    // ========================================
    // E2E TESTS (full environment)
    // ========================================
    var allContainerizedServices = manifest.Services
        .Select(s => containerized[s.Name]);

    var allServices = manifest.Services
        .Select(s => new Service(builtImages[s.Name], s.Name, ports: [8080]));

    var e2eTests = graph.Add("test-e2e",
        image: "mcr.microsoft.com/playwright:latest",
        needs: allContainerizedServices,
        services: allServices,
        run: async ctx =>
        {
            await ctx.WaitForHealthy("http://api-gateway:8080/health");
            await ctx.Exec("npx", "playwright", "test", "--reporter=html");
            ctx.Output("playwright-report", "playwright-report/");
        });

    testJobs.Add(e2eTests);

    // ========================================
    // SECURITY SCANNING (parallel with tests)
    // ========================================
    foreach (var svc in manifest.Services)
    {
        scanJobs[svc.Name] = graph.Add($"scan-{svc.Name}",
            image: "aquasec/trivy:latest",
            needs: [containerized[svc.Name]],
            run: async ctx =>
            {
                await ctx.Exec("trivy", "image",
                    "--exit-code", "1",
                    "--severity", "CRITICAL,HIGH",
                    "--format", "json",
                    "--output", "trivy-report.json",
                    builtImages[svc.Name]);
                ctx.Output("scan-results", "trivy-report.json");
            });

        testJobs.Add(scanJobs[svc.Name]);
    }

    // ========================================
    // QUALITY GATE (fan-in)
    // ========================================
    var qualityGate = graph.Add("quality-gate",
        image: "alpine:latest",
        needs: testJobs,
        run: ctx => ctx.Exec("echo", "All quality checks passed"));

    // ========================================
    // PUSH & DEPLOY (only on main)
    // ========================================
    if (ctx.Branch == "main")
    {
        // Push all images
        var pushJobs = new Dictionary<string, JobNode>();
        foreach (var svc in manifest.Services)
        {
            pushJobs[svc.Name] = graph.Add($"push-{svc.Name}",
                image: "gcr.io/go-containerregistry/crane:latest",
                needs: [qualityGate],
                run: async ctx =>
                {
                    var source = builtImages[svc.Name];
                    await ctx.Exec("crane", "push", source, $"registry.io/{svc.Name}:${{BUILD_ID}}");
                    await ctx.Exec("crane", "push", source, $"registry.io/{svc.Name}:latest");
                });
        }

        // Staged deployment - track deploy and verify nodes
        var deployJobs = new Dictionary<string, JobNode>();
        var verifyJobs = new Dictionary<string, JobNode>();
        var environments = new[] { "staging", "canary", "production" };
        string? previousEnv = null;

        foreach (var env in environments)
        {
            IEnumerable<JobNode> needs = previousEnv == null
                ? pushJobs.Values
                : new[] { deployJobs[previousEnv], verifyJobs[previousEnv] };

            // Production requires approval
            if (env == "production")
            {
                var approveProduction = graph.Add("approve-production",
                    needs: [verifyJobs["canary"]],
                    approval: new ApprovalConfig
                    {
                        Required = true,
                        Approvers = ["platform-team", "oncall"],
                        Timeout = TimeSpan.FromHours(24),
                        Message = "Approve production deployment? Canary has been verified."
                    });

                needs = [approveProduction];
            }

            deployJobs[env] = graph.Add($"deploy-{env}",
                image: "bitnami/kubectl:latest",
                needs: needs,
                env: new Dictionary<string, string>
                {
                    ["KUBECONFIG"] = ctx.Secret($"kubeconfig-{env}")
                },
                run: async ctx =>
                {
                    await ctx.Exec("kubectl", "apply", "-k", $"k8s/overlays/{env}");
                    await ctx.Exec("kubectl", "rollout", "status",
                        "deployment", "--all", "--timeout=5m");
                });

            verifyJobs[env] = graph.Add($"verify-{env}",
                image: "curlimages/curl:latest",
                needs: [deployJobs[env]],
                run: async ctx =>
                {
                    var endpoint = manifest.Environments[env].HealthEndpoint;
                    await ctx.Retry(5, TimeSpan.FromSeconds(10), async () =>
                    {
                        await ctx.Exec("curl", "-f", "-s", endpoint);
                    });
                });

            previousEnv = env;
        }
    }

    return graph;
});

// Notify on any failure
OnFailure(async ctx =>
{
    var webhook = ctx.Secret("discord-webhook");
    await ctx.Discord(webhook).Send(new DiscordMessage
    {
        Title = "Pipeline Failed",
        Description = $"Failed job: {ctx.FailedJob}\n{ctx.FailureMessage}",
        Link = ctx.BuildUrl,
        Color = DiscordColor.Danger,
        Fields = [
            new("Branch", ctx.Branch),
            new("Commit", ctx.Commit[..8]),
            new("Author", ctx.CommitAuthor)
        ]
    });
});
