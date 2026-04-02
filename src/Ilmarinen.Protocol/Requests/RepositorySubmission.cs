namespace Ilmarinen.Protocol.Requests;

public record RepositorySubmission
{
    public required string Name { get; init; }
    public required string RepoUrl { get; init; }
    public string? GitToken { get; init; }
}
