using Ilmarinen.Protocol.Requests;
using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// Decodes the StreamLogs chunks a LogCollector parked in a MessageBuffer (its connection being down) back into the (type, data) entries they carry.
/// </summary>
public static class LogChunkDrain
{
    public static List<(string Type, string Data)> DrainEntries(MessageBuffer buffer)
    {
        var entries = new List<(string, string)>();

        while (buffer.TryDequeue(out var message))
        {
            Assert.That(message!.Method, Is.EqualTo("StreamLogs"));
            var chunk = (LogChunk)message.Args.Single();

            foreach (var line in chunk.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line);
                entries.Add((doc.RootElement.GetProperty("t").GetString()!, doc.RootElement.GetProperty("d").GetString()!));
            }
        }

        return entries;
    }
}
