using Ilmarinen.Execution;
using Ilmarinen.Models;

namespace Ilmarinen.Scripting;

/// <summary>
/// Globals available to pipeline scripts.
/// </summary>
public class ScriptGlobals
{
    private readonly List<Step<object?>> _steps = [];

    /// <summary>
    /// All steps defined in the script.
    /// </summary>
    public IReadOnlyList<Step<object?>> Steps => _steps;

    /// <summary>
    /// Define a new step with no typed output.
    /// </summary>
    public ScriptStepBuilder Step(string name)
    {
        return new ScriptStepBuilder(name, _steps);
    }

    /// <summary>
    /// Define a new step with typed output.
    /// </summary>
    public ScriptStepBuilder<T> Step<T>(string name)
    {
        return new ScriptStepBuilder<T>(name, _steps);
    }
}

/// <summary>
/// Step builder for scripts with typed output that auto-collects steps.
/// </summary>
public class ScriptStepBuilder<T>
{
    private readonly string _name;
    private readonly List<Step<object?>> _steps;
    private Func<ImageRef>? _imageResolver;

    internal ScriptStepBuilder(string name, List<Step<object?>> steps)
    {
        _name = name;
        _steps = steps;
    }

    public ScriptStepBuilder<T> Image(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        var img = ImageRef.From(image);
        _imageResolver = () => img;
        return this;
    }

    public ScriptStepBuilder<T> Image(Func<ImageRef> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = resolver;
        return this;
    }

    public ScriptStepBuilder<T> Image(Func<string> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _imageResolver = () => ImageRef.From(resolver());
        return this;
    }

    public Step<T> Run(Func<IJobContext, Task<T>> action)
    {
        if (_imageResolver == null)
            throw new InvalidOperationException($"Step '{_name}' must have an image.");

        var step = new Step<T>
        {
            Name = _name,
            ImageResolver = _imageResolver,
            Action = action
        };

        // Store as base type for collection
        _steps.Add(new Step<object?>
        {
            Name = step.Name,
            ImageResolver = step.ImageResolver,
            Action = async ctx =>
            {
                var result = await step.Action(ctx);
                step.Output = result;
                step.HasRun = true;
                return result;
            }
        });

        return step;
    }
}

/// <summary>
/// Step builder for scripts with no typed output that auto-collects steps.
/// </summary>
public class ScriptStepBuilder : ScriptStepBuilder<object?>
{
    internal ScriptStepBuilder(string name, List<Step<object?>> steps) : base(name, steps) { }

    public new ScriptStepBuilder Image(string image)
    {
        base.Image(image);
        return this;
    }

    public new ScriptStepBuilder Image(Func<ImageRef> resolver)
    {
        base.Image(resolver);
        return this;
    }

    public new ScriptStepBuilder Image(Func<string> resolver)
    {
        base.Image(resolver);
        return this;
    }

    public Step Run(Func<IJobContext, Task> action)
    {
        var step = base.Run(async ctx =>
        {
            await action(ctx);
            return null;
        });

        return new Step
        {
            Name = step.Name,
            ImageResolver = step.ImageResolver,
            Action = step.Action,
            Output = step.Output,
            HasRun = step.HasRun
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
