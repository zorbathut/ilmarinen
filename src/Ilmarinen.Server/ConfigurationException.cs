namespace Ilmarinen.Server;

/// <summary>
/// Thrown when a required server configuration is missing or invalid.
/// Results in HTTP 503 Service Unavailable with a helpful message.
/// </summary>
public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}
