using Ilmarinen.Execution;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Models;

/// <summary>
/// A pipeline step that runs in a container and produces an output of type T.
/// </summary>
public class Step<T>
{
    public required string Name { get; init; }

    /// <summary>
    /// Resolves the container image for this step. Called at runtime.
    /// </summary>
    public required Func<ImageRef> ImageResolver { get; init; }

    /// <summary>
    /// The action to run. Returns the step's output.
    /// </summary>
    public required Func<IJobContext, Task<T>> Action { get; init; }

    /// <summary>
    /// The output from this step (populated after execution).
    /// </summary>
    public T? Output { get; set; }

    /// <summary>
    /// Whether this step has been executed.
    /// </summary>
    public bool HasRun { get; set; }
}

/// <summary>
/// A pipeline step with no typed output.
/// </summary>
public class Step : Step<object?>
{
}
