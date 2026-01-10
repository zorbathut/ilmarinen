namespace Ilmarinen.Execution;

/// <summary>
/// Thrown when a command execution fails (non-zero exit code).
/// </summary>
public class CommandException : Exception
{
    private const int MaxStderrPreview = 500;
    private const int MaxOutputLength = 4096;

    /// <summary>
    /// The command that was executed.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// The arguments passed to the command.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// The exit code from the command.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Standard output from the command (may be truncated).
    /// </summary>
    public string Stdout { get; }

    /// <summary>
    /// Standard error from the command (may be truncated).
    /// </summary>
    public string Stderr { get; }

    /// <summary>
    /// The full CommandResult, if available.
    /// </summary>
    public CommandResult? Result { get; }

    public CommandException(
        string command,
        IReadOnlyList<string> arguments,
        int exitCode,
        string stdout,
        string stderr,
        CommandResult? result = null)
        : base(BuildMessage(command, arguments, exitCode, stderr))
    {
        Command = command;
        Arguments = arguments;
        ExitCode = exitCode;
        Stdout = Truncate(stdout, MaxOutputLength);
        Stderr = Truncate(stderr, MaxOutputLength);
        Result = result;
    }

    private static string BuildMessage(string command, IReadOnlyList<string> args, int exitCode, string stderr)
    {
        var cmdLine = args.Count > 0 ? $"{command} {string.Join(" ", args)}" : command;
        var msg = $"Command '{cmdLine}' failed with exit code {exitCode}";
        var stderrPreview = Truncate(stderr, MaxStderrPreview);
        if (!string.IsNullOrWhiteSpace(stderrPreview))
            msg += $"\nStderr: {stderrPreview}";
        return msg;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..(maxLength - 20)] + $"\n... ({value.Length - maxLength + 20} chars truncated)";
    }
}

/// <summary>
/// Thrown when a shell script execution fails.
/// </summary>
public class ShellException : CommandException
{
    private const int MaxScriptPreview = 200;

    /// <summary>
    /// The original shell script that was executed.
    /// </summary>
    public string Script { get; }

    public ShellException(string script, int exitCode, string stdout, string stderr, CommandResult? result = null)
        : base("/bin/sh", ["-c", TruncateScript(script, MaxScriptPreview)], exitCode, stdout, stderr, result)
    {
        Script = script;
    }

    private static string TruncateScript(string script, int maxLength)
    {
        if (script.Length <= maxLength) return script;
        return script[..maxLength] + "...";
    }
}

/// <summary>
/// Thrown when a nested container execution fails.
/// </summary>
public class NestedContainerException : Exception
{
    private const int MaxStderrPreview = 500;

    /// <summary>
    /// The image that was run.
    /// </summary>
    public string Image { get; }

    /// <summary>
    /// The command executed in the container.
    /// </summary>
    public IReadOnlyList<string> Command { get; }

    /// <summary>
    /// The exit code from the container.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Standard output from the container.
    /// </summary>
    public string Stdout { get; }

    /// <summary>
    /// Standard error from the container.
    /// </summary>
    public string Stderr { get; }

    /// <summary>
    /// The nesting depth (1 = first nested container, 2 = nested within nested, etc.)
    /// </summary>
    public int NestingDepth { get; }

    /// <summary>
    /// Chain of container images from outermost to innermost.
    /// </summary>
    public IReadOnlyList<string> ContainerChain { get; }

    public NestedContainerException(
        string image,
        IReadOnlyList<string> command,
        int exitCode,
        string stdout,
        string stderr,
        int nestingDepth = 1,
        IReadOnlyList<string>? containerChain = null,
        Exception? inner = null)
        : base(BuildMessage(image, command, exitCode, stderr, nestingDepth), inner)
    {
        Image = image;
        Command = command;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        NestingDepth = nestingDepth;
        ContainerChain = containerChain ?? [image];
    }

    private static string BuildMessage(string image, IReadOnlyList<string> command, int exitCode, string stderr, int depth)
    {
        var cmdStr = command.Count > 0 ? string.Join(" ", command) : "(default entrypoint)";
        var depthInfo = depth > 1 ? $" (nesting depth: {depth})" : "";
        var msg = $"Nested container '{image}' failed{depthInfo} with exit code {exitCode}\nCommand: {cmdStr}";
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var stderrPreview = stderr.Length > MaxStderrPreview ? stderr[..MaxStderrPreview] + "..." : stderr;
            msg += $"\nStderr: {stderrPreview}";
        }
        return msg;
    }
}
