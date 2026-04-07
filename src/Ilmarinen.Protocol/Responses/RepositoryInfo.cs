using System;

namespace Ilmarinen.Protocol.Responses;

public record RepositoryInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string RepoUrl { get; init; }
    public bool HasGitToken { get; init; }
    public DateTime CreatedAt { get; init; }
}
