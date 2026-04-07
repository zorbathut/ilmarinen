using System;

namespace Ilmarinen.Database.Entities;

public class Repository
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string RepoUrl { get; set; }
    public string? EncryptedGitToken { get; set; }
    public DateTime CreatedAt { get; set; }
}
