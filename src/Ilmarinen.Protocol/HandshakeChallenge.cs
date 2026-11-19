using System.Text;
using System;

namespace Ilmarinen.Protocol;

/// <summary>The bytes both sides of the worker handshake sign. They bind the connection's instance and counter, so a captured handshake can't be replayed as a different connection.</summary>
public static class HandshakeChallenge
{
    public static byte[] Build(Guid workerId, Guid instanceId, long connectionCounter, byte[] workerNonce, byte[] serverNonce)
    {
        return Encoding.UTF8.GetBytes($"{workerId}:{instanceId}:{connectionCounter}:{Convert.ToBase64String(workerNonce)}:{Convert.ToBase64String(serverNonce)}");
    }
}
