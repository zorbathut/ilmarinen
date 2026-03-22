using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class JobArtifact
{
    public required Ulid Id { get; set; }
    public required Ulid JobId { get; set; }
    public required string Name { get; set; }
    public required string RelativePath { get; set; }
    public required long Size { get; set; }
    public DateTime CreatedAt { get; set; }

    public Job? Job { get; set; }
}
