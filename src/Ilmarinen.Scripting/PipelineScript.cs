using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Ilmarinen.Models;

namespace Ilmarinen.Scripting;

/// <summary>
/// Loads and executes pipeline scripts.
/// </summary>
public class PipelineScript
{
    /// <summary>
    /// Load a pipeline from a .csx file.
    /// </summary>
    public static async Task<IReadOnlyList<Step<object?>>> LoadAsync(string path)
    {
        var code = await File.ReadAllTextAsync(path);
        return await LoadFromStringAsync(code);
    }

    /// <summary>
    /// Load a pipeline from script code.
    /// </summary>
    public static async Task<IReadOnlyList<Step<object?>>> LoadFromStringAsync(string code)
    {
        var globals = new ScriptGlobals();

        var options = ScriptOptions.Default
            .AddReferences(typeof(Step).Assembly)
            .AddImports(
                "System",
                "System.Threading.Tasks",
                "Ilmarinen.Models",
                "Ilmarinen.Execution");

        await CSharpScript.RunAsync(code, options, globals);

        return globals.Steps;
    }
}
