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
    WorkerDnsFailure,
    ContainerDnsFailure,
    AgentApiUnreachable,
    Timeout
}
