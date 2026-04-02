using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class Pipeline
{
    public required Ulid Id { get; set; }
    public required string Name { get; set; }
    public required Ulid RepositoryId { get; set; }
    public required string DefaultRef { get; set; }
    public required string ScriptPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Schedule { get; set; }
    public DateTime? LastTriggeredAt { get; set; }

    public Repository? Repository { get; set; }
}
