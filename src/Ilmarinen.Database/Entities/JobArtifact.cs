using System;

namespace Ilmarinen.Database.Entities;

public class JobArtifact
{
    public required Guid Id { get; set; }
    public required Guid JobId { get; set; }
    public required string Name { get; set; }
    public required string RelativePath { get; set; }
    public required long Size { get; set; }
    public DateTime CreatedAt { get; set; }

    public Job? Job { get; set; }
}
