// Integration Testing Pipeline
//
// Demonstrates running your application as a service alongside tests. Shows how to use built images as sidecars and wait for health checks.

#r "Conductor"

// Build the application
var build = Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        await ctx.Exec("dotnet", "publish", "-c", "Release", "-o", "out");
        return ctx.BuildImage("Dockerfile");
    });

// Unit tests (no external dependencies)
var testUnit = Step("test-unit")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(ctx => ctx.Exec("dotnet", "test", "--filter", "Category!=Integration"));

// Integration tests with database
var testIntegration = Step("test-integration")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Needs(build)
    .Service(new ServiceConfig
    {
        Image = "postgres:15",
        Hostname = "db",
        Environment = new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = "test",
            ["POSTGRES_DB"] = "testdb"
        }
    })
    .Run(async ctx =>
    {
        await ctx.WaitForHealthy("tcp://db:5432");
        await ctx.Exec("dotnet", "test", "--filter", "Category=Integration")
            .Env("ConnectionStrings__Default", "Host=db;Database=testdb;Username=postgres;Password=test");
    });

// E2E tests - run the app as a service and hit it with Playwright
var testE2e = Step("test-e2e")
    .Image("mcr.microsoft.com/playwright:latest")
    .Needs(build)
    .Service(build, "app", ports: [8080])
    .Service("postgres:15", "db", environment: new Dictionary<string, string>
    {
        ["POSTGRES_PASSWORD"] = "test",
        ["POSTGRES_DB"] = "appdb"
    })
    .Run(async ctx =>
    {
        // Wait for the app to be ready
        await ctx.WaitForHealthy("http://app:8080/health", new HealthCheckOptions
        {
            Timeout = TimeSpan.FromMinutes(2),
            Interval = TimeSpan.FromSeconds(5)
        });
        
        // Run Playwright tests
        await ctx.Exec("npx", "playwright", "test")
            .Env("BASE_URL", "http://app:8080");
        
        // Upload test results
        ctx.Output("playwright-report", "playwright-report/");
    });

// All tests must pass before deploy
Step("deploy")
    .Image("bitnami/kubectl:latest")
    .Needs(testUnit, testIntegration, testE2e)
    .When(ctx => ctx.Branch == "main")
    .Run(ctx => ctx.Exec("kubectl", "apply", "-f", "k8s/"));
