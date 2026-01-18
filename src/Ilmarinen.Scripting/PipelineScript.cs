using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Ilmarinen.Models;

namespace Ilmarinen.Scripting;

/// <summary>
/// Result of loading a pipeline script.
/// </summary>
public record PipelineScriptResult(
    IReadOnlyList<Step<object?>> Steps,
    WorkspaceConfig? Workspace);

/// <summary>
/// Loads and executes pipeline scripts.
/// </summary>
public class PipelineScript
{
    /// <summary>
    /// Load a pipeline from a .csx file.
    /// </summary>
    public static async Task<PipelineScriptResult> LoadAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var scriptDir = Path.GetDirectoryName(fullPath)!;
        var code = await File.ReadAllTextAsync(fullPath);

        var globals = new ScriptGlobals();

        var options = ScriptOptions.Default
            .AddReferences(typeof(Step).Assembly)
            .AddImports(
                "System",
                "System.Threading.Tasks",
                "Ilmarinen.Models",
                "Ilmarinen.Execution")
            .WithFilePath(fullPath)
            .WithSourceResolver(new SourceFileResolver(searchPaths: [], baseDirectory: scriptDir));

        await CSharpScript.RunAsync(code, options, globals);

        return new PipelineScriptResult(globals.Steps, globals.WorkspaceConfig);
    }

    /// <summary>
    /// Load a pipeline from script code.
    /// </summary>
    public static async Task<PipelineScriptResult> LoadFromStringAsync(string code)
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

        return new PipelineScriptResult(globals.Steps, globals.WorkspaceConfig);
    }
}
