using Ilmarinen.Execution;

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

/// <summary>
/// Fluent builder for creating steps with typed output.
/// </summary>
public class StepBuilder<T>
{
    private readonly string _name;
    private Func<ImageRef>? _imageResolver;

    public StepBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public StepBuilder<T> Image(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        var img = ImageRef.From(image);
        _imageResolver = () => img;
        return this;
    }

    public StepBuilder<T> Image(Func<ImageRef> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = resolver;
        return this;
    }

    public StepBuilder<T> Image(Func<string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = () => ImageRef.From(resolver());
        return this;
    }

    public Step<T> Run(Func<IJobContext, Task<T>> action)
    {
        if (_imageResolver == null)
            throw new InvalidOperationException($"Step '{_name}' must have an image.");

        return new Step<T>
        {
            Name = _name,
            ImageResolver = _imageResolver,
            Action = action
        };
    }
}

/// <summary>
/// Fluent builder for creating steps with no typed output.
/// </summary>
public class StepBuilder
{
    private readonly string _name;
    private Func<ImageRef>? _imageResolver;

    public StepBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public StepBuilder Image(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        var img = ImageRef.From(image);
        _imageResolver = () => img;
        return this;
    }

    public StepBuilder Image(Func<ImageRef> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = resolver;
        return this;
    }

    public StepBuilder Image(Func<string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = () => ImageRef.From(resolver());
        return this;
    }

    public Step Run(Func<IJobContext, Task> action)
    {
        if (_imageResolver == null)
            throw new InvalidOperationException($"Step '{_name}' must have an image.");

        return new Step
        {
            Name = _name,
            ImageResolver = _imageResolver,
            Action = async ctx =>
            {
                await action(ctx);
                return null;
            }
        };
    }

    public Step Run(Action<IJobContext> action)
    {
        return Run(ctx =>
        {
            action(ctx);
            return Task.CompletedTask;
        });
    }
}
