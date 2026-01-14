using NUlid;

namespace Ilmarinen.Database.Entities;

public class Worker
{
    public required Ulid Id { get; set; }
    public bool IsConnected { get; set; }
    public bool IsReady { get; set; }
    public Ulid? CurrentJobId { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}
