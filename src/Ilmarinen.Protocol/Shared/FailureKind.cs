namespace Ilmarinen.Protocol;

public enum FailureKind
{
    None,
    Unknown,
    SocketUnreachable,
    SocketPermission,
    DaemonError,
    RegistryUnreachable,
    ContainerCreateFailed,
    ContainerExecFailed,
    OutputMismatch,
    ContainerDnsFailure,
    AgentApiUnreachable,
    Timeout
}
