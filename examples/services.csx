// Service containers demo - running a database for tests

Step("service-test")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        Console.WriteLine("=== Starting Redis service ===");

        // Start Redis as a background service
        var redis = await ctx.StartService("redis:alpine", "redis", [6379]);
        Console.WriteLine($"Started service: {redis.Name}");

        try
        {
            // Wait for Redis to be ready
            Console.WriteLine("Waiting for Redis to be healthy...");
            await ctx.WaitForHealthy("tcp://redis:6379", TimeSpan.FromSeconds(30));
            Console.WriteLine("Redis is ready!");

            // Run a container that talks to Redis
            Console.WriteLine("\n=== Testing Redis connection ===");
            await ctx.Run("redis:alpine", "redis-cli", "-h", "redis", "PING");
            await ctx.Run("redis:alpine", "redis-cli", "-h", "redis", "SET", "test-key", "hello-ilmarinen");
            await ctx.Run("redis:alpine", "redis-cli", "-h", "redis", "GET", "test-key");

            Console.WriteLine("\n=== Service test passed! ===");
        }
        finally
        {
            // Clean up the service
            Console.WriteLine("\nStopping Redis service...");
            await redis.StopAsync();
        }
    });
