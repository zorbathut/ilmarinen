namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Challenge for ECDSA authentication.
/// </summary>
public record AuthChallenge
{
    public required byte[] Nonce { get; init; }
    public required byte[] ServerSignature { get; init; }
    public required string ProtocolHash { get; init; }

    /// <summary>
    /// Hash of the worker bundle the server currently serves. Null when the server has no bundle configured, which means "no opinion" — a launcher-run worker must never treat it as stale.
    /// </summary>
    public string? CurrentBundleHash { get; init; }
}
