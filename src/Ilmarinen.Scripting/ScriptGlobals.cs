using Ilmarinen.Execution;
using Ilmarinen.Models;

namespace Ilmarinen.Scripting;

/// <summary>
/// Globals available to pipeline scripts.
/// </summary>
public class ScriptGlobals
{
    private readonly List<Step> _steps = [];

    /// <summary>
    /// All steps defined in the script.
    /// </summary>
    public IReadOnlyList<Step> Steps => _steps;

    /// <summary>
    /// Define a new step.
    /// </summary>
    public ScriptStepBuilder Step(string name)
    {
        return new ScriptStepBuilder(name, _steps);
    }
}

/// <summary>
/// Step builder for scripts that auto-collects steps.
/// </summary>
public class ScriptStepBuilder
{
    private readonly string _name;
    private readonly List<Step> _steps;
    private string? _image;

    internal ScriptStepBuilder(string name, List<Step> steps)
    {
        _name = name;
        _steps = steps;
    }

    public ScriptStepBuilder Image(string image)
    {
        _image = image;
        return this;
    }

    public Step Run(Func<IJobContext, Task> action)
    {
        if (string.IsNullOrWhiteSpace(_image))
            throw new InvalidOperationException($"Step '{_name}' must have an image.");

        var step = new Step
        {
            Name = _name,
            Image = _image,
            Action = action
        };
        _steps.Add(step);
        return step;
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
