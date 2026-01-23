// Test that ilmarinen-agent run propagates exit codes correctly

Step("run-exit-codes")
    .Image("docker:cli")
    .Run(async ctx =>
    {
        var passed = 0;
        var failed = 0;

        void Check(string label, int expected, int actual)
        {
            if (expected == actual)
            {
                Console.WriteLine($"  PASS: expected={expected} actual={actual}");
                passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: expected={expected} actual={actual}");
                failed++;
            }
        }

        // Test 1: Success (exit 0)
        Console.WriteLine("=== Test 1: exit 0 (success) ===");
        var r = await ctx.TryRun("alpine:latest", "true");
        Check("exit 0", 0, r.ExitCode);

        // Test 2: exit 1 (standard failure)
        Console.WriteLine("=== Test 2: exit 1 (standard failure) ===");
        r = await ctx.TryRun("alpine:latest", "false");
        Check("exit 1", 1, r.ExitCode);

        // Test 3: exit 42 (specific code)
        Console.WriteLine("=== Test 3: exit 42 (specific code) ===");
        r = await ctx.TryRun("alpine:latest", "sh", "-c", "exit 42");
        Check("exit 42", 42, r.ExitCode);

        // Test 4: exit 127 (command not found)
        Console.WriteLine("=== Test 4: exit 127 (command not found) ===");
        r = await ctx.TryRun("alpine:latest", "sh", "-c", "nonexistent_command_xyz");
        Check("exit 127", 127, r.ExitCode);

        // Test 5: exit 130 (simulating SIGINT / Ctrl+C: 128 + 2)
        Console.WriteLine("=== Test 5: exit 130 (SIGINT simulation) ===");
        r = await ctx.TryRun("alpine:latest", "sh", "-c", "exit 130");
        Check("exit 130", 130, r.ExitCode);

        Console.WriteLine($"\n=== Results: {passed} passed, {failed} failed ===");
        if (failed > 0)
            throw new Exception($"{failed} exit code test(s) failed");
    });
