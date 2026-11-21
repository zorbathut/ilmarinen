namespace Ilmarinen.Protocol;

/// <summary>What a run's pipeline worked in.</summary>
public enum WorkspaceKind
{
    /// <summary>The run ended before its script said: the script didn't load or compile, say.</summary>
    Unknown,

    /// <summary>A checkout made for the run and deleted after it.</summary>
    Ephemeral,

    /// <summary>A persistent workspace the script names, kept on the worker between runs.</summary>
    Named
}
