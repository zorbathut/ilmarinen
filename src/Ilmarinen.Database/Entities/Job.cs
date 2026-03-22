using Ilmarinen.Protocol;
using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class Job
{
    public required Ulid Id { get; set; }
    public required JobStatus Status { get; set; }
    public required string RepoUrl { get; set; }
    public required string Ref { get; set; }
    public required string ScriptPath { get; set; }
    public Ulid? WorkerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? EncryptedGitToken { get; set; }
    public Ulid? PipelineId { get; set; }
}
