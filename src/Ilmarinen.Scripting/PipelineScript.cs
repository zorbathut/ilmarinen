using Ilmarinen.Models;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

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

        return await LoadAsync(fullPath, new SourceFileResolver(searchPaths: [], baseDirectory: scriptDir));
    }

    /// <summary>
    /// Load a pipeline from the script <paramref name="sourceResolver"/> serves at <paramref name="filePath"/>, with its <c>#load</c> directives resolved by the same resolver.
    /// </summary>
    public static async Task<PipelineScriptResult> LoadAsync(string filePath, SourceReferenceResolver sourceResolver)
    {
        var code = sourceResolver.ReadText(filePath).ToString();

        return await RunAsync(code, CreateOptions().WithFilePath(filePath).WithSourceResolver(sourceResolver));
    }

    /// <summary>
    /// Load a pipeline from script code.
    /// </summary>
    public static async Task<PipelineScriptResult> LoadFromStringAsync(string code)
    {
        return await RunAsync(code, CreateOptions());
    }

    private static ScriptOptions CreateOptions()
    {
        return ScriptOptions.Default
            .AddReferences(typeof(Step).Assembly)
            .AddImports(
                "System",
                "System.Threading.Tasks",
                "Ilmarinen.Models",
                "Ilmarinen.Execution");
    }

    private static async Task<PipelineScriptResult> RunAsync(string code, ScriptOptions options)
    {
        var globals = new ScriptGlobals();

        await CSharpScript.RunAsync(code, options, globals);

        return new PipelineScriptResult(globals.Steps, globals.WorkspaceConfig);
    }
}
