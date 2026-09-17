using Discord;
using Ilmarinen.Protocol.Responses;
using System.Linq;
using System;

namespace Ilmarinen.DiscordBot;

/// <summary>
/// The Discord messages announcing that queued jobs are stuck, and that they no longer are.
/// </summary>
public static class WedgeEmbeds
{
    private const int MaxListedJobs = 10;
    private const int MaxTextLength = 100;

    /// <summary>
    /// Text taken from jobs is truncated, so no ref or pipeline name can push the embed past Discord's size limits: a post rejected for that would be rejected again on every retry.
    /// </summary>
    public static Embed Wedged(UnsatisfiableJobsReport report, Func<Guid, string?> jobUrl)
    {
        var count = report.Jobs.Count;
        var cause = report.HighestAvailableWorkerPriority is { } highest
            ? $"The highest available worker priority is {highest}."
            : "No worker is available at all.";

        var embed = new EmbedBuilder()
            .WithTitle("Jobs Stuck")
            .WithColor(Color.Red)
            .WithCurrentTimestamp()
            .WithDescription($"{count} queued {(count == 1 ? "job needs" : "jobs need")} a worker that isn't available. {cause}");

        foreach (var job in report.Jobs.Take(MaxListedJobs))
        {
            // Discord rejects a blank field name, and nothing stops a pipeline from being named with whitespace.
            var name = string.IsNullOrWhiteSpace(job.PipelineName) ? job.RepoUrl : job.PipelineName;
            var details = $"{Truncate(Format.Sanitize(job.Ref))} · needs {job.MinWorkerPriority}";
            var url = jobUrl(job.Id);
            embed.AddField(Truncate(name), url != null ? $"{details} · [view]({url})" : $"{details} · {job.Id}");
        }

        if (count > MaxListedJobs)
        {
            embed.WithFooter($"…and {count - MaxListedJobs} more");
        }

        return embed.Build();
    }

    public static Embed Unwedged()
    {
        return new EmbedBuilder()
            .WithTitle("Jobs No Longer Stuck")
            .WithColor(Color.Green)
            .WithCurrentTimestamp()
            .WithDescription("No queued job is waiting on a worker that isn't available.")
            .Build();
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
    }
}
