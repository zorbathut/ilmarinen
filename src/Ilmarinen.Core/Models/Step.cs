using Ilmarinen.Execution;

namespace Ilmarinen.Models;

/// <summary>
/// A pipeline step that runs in a container.
/// </summary>
public sealed class Step
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required Func<IJobContext, Task> Action { get; init; }
}

/// <summary>
/// Fluent builder for creating steps.
/// </summary>
public class StepBuilder
{
    private readonly string _name;
    private string? _image;

    public StepBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public StepBuilder Image(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        _image = image;
        return this;
    }

    public Step Run(Func<IJobContext, Task> action)
    {
        if (string.IsNullOrWhiteSpace(_image))
            throw new InvalidOperationException($"Step '{_name}' must have an image.");

        return new Step
        {
            Name = _name,
            Image = _image,
            Action = action
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
