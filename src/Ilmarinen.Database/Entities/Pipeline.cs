using NUlid;

namespace Ilmarinen.Database.Entities;

public class Pipeline
{
    public required Ulid Id { get; set; }
    public required string Name { get; set; }
    public required string RepoUrl { get; set; }
    public required string DefaultRef { get; set; }
    public required string ScriptPath { get; set; }
    public string? EncryptedGitToken { get; set; }
    public DateTime CreatedAt { get; set; }
}
