namespace Ilmarinen.Models;

/// <summary>
/// Configuration for a persistent workspace that survives between job runs.
/// </summary>
public record WorkspaceConfig(string Name)
{
    /// <summary>
    /// Validates that a workspace name is safe (no path traversal, only allowed characters).
    /// </summary>
    /// <param name="name">The workspace name to validate.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if the name is valid, false otherwise.</returns>
    public static bool IsValidName(string name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Workspace name cannot be null or empty.";
            return false;
        }

        // Reject path separators and parent directory references
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            error = $"Workspace name '{name}' contains invalid characters. " +
                    "Path separators (/, \\) and '..' are not allowed.";
            return false;
        }

        // Reject absolute paths (starts with / or drive letter)
        if (Path.IsPathRooted(name))
        {
            error = $"Workspace name '{name}' cannot be an absolute path.";
            return false;
        }

        // Reject leading dot (hidden files/directories)
        if (name.StartsWith('.'))
        {
            error = $"Workspace name '{name}' cannot start with a dot.";
            return false;
        }

        // Only allow alphanumeric, dash, underscore, and dot
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
            {
                error = $"Workspace name '{name}' contains invalid character '{c}'. " +
                        "Only alphanumeric characters, dashes, underscores, and dots are allowed.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a workspace name and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the name is invalid.</exception>
    public static void ValidateName(string name)
    {
        if (!IsValidName(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }
    }
}
