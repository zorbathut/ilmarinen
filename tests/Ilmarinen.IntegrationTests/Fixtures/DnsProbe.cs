using System.Net.Sockets;
using System.Net;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Whether this host's resolver is working, for the tests that run the real worker diagnostic: it resolves the registry's name and fails the whole run if it can't, which says nothing about the code under test.
/// </summary>
public static class DnsProbe
{
    public static bool CanResolveRegistry()
    {
        try
        {
            Dns.GetHostEntry("docker.io");
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
