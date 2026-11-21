using Discord;
using Ilmarinen.Protocol.Responses;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.DiscordBot;

/// <summary>
/// The Discord messages announcing that queued jobs are stuck, and that they no longer are.
/// </summary>
public static class WedgeEmbeds
{
    private const int MaxListedJobs = 10;
    private const int MaxListedWorkers = 10;
    private const int MaxTextLength = 100;

    /// <summary>
    /// Text taken from jobs is truncated, so no ref or pipeline name can push the embed past Discord's size limits: a post rejected for that would be rejected again on every retry.
    /// </summary>
    public static Embed Wedged(UnsatisfiableJobsReport report, Func<Guid, string?> jobUrl)
    {
        var count = report.Jobs.Count;
        var noRoom = report.NoRoomJobs.Count;
        var cause = report.HighestAvailableWorkerPriority is { } highest
            ? $"The highest available worker priority is {Truncate(Format.Sanitize(highest.Name))}."
            : "No worker is available at all.";
        if (report.PausedWorkers.Count > 0)
        {
            cause += $" Paused workers that could take them: {Names(report.PausedWorkers)}.";
        }
        if (report.InUseWorkers.Count > 0)
        {
            cause += $" Workers whose machines are in use that could take them: {Names(report.InUseWorkers)}.";
        }
        if (report.UnreachableWorkers.Count > 0)
        {
            cause += $" Sleeping workers that didn't wake when woken: {Names(report.UnreachableWorkers)}.";
        }

        var description = new List<string>();
        if (count > 0)
        {
            description.Add($"{count} queued {(count == 1 ? "job needs" : "jobs need")} a worker that isn't available. {cause}");
        }
        if (noRoom > 0)
        {
            description.Add($"{noRoom} queued {(noRoom == 1 ? "job needs" : "jobs need")} more disk space than any worker that could take {(noRoom == 1 ? "it" : "them")} has, even after deleting its workspaces. Free some space on a worker (Docker images and build cache, say).");
        }

        var embed = new EmbedBuilder()
            .WithTitle("Jobs Stuck")
            .WithColor(Color.Red)
            .WithCurrentTimestamp()
            .WithDescription(string.Join(" ", description));

        var listed = report.Jobs.Select(job => (Job: job, Details: $"needs {(job.MinWorkerPriority is { } minimum ? Truncate(Format.Sanitize(minimum.Name)) : "any worker")}"))
            .Concat(report.NoRoomJobs.Select(n => (n.Job, Details: $"needs {Size(n.NeededBytes)} of disk{(n.MostRoomBytes is { } most ? $", at most {Size(most)} to be had" : "")}")))
            .ToList();
        foreach (var (job, needs) in listed.Take(MaxListedJobs))
        {
            // Discord rejects a blank field name, and nothing stops a pipeline from being named with whitespace.
            var name = string.IsNullOrWhiteSpace(job.PipelineName) ? job.RepoUrl : job.PipelineName;
            var details = $"{Truncate(Format.Sanitize(job.Ref))} · {needs}";
            var url = jobUrl(job.Id);
            embed.AddField(Truncate(name), url != null ? $"{details} · [view]({url})" : $"{details} · {job.Id}");
        }

        if (listed.Count > MaxListedJobs)
        {
            embed.WithFooter($"…and {listed.Count - MaxListedJobs} more");
        }

        return embed.Build();
    }

    private static string Size(long bytes)
    {
        return $"{Math.Max(0, bytes) / (1024.0 * 1024 * 1024):0.#} GB";
    }

    public static Embed Unwedged()
    {
        return new EmbedBuilder()
            .WithTitle("Jobs No Longer Stuck")
            .WithColor(Color.Green)
            .WithCurrentTimestamp()
            .WithDescription("No queued job is waiting on a worker that isn't available, or on disk space.")
            .Build();
    }

    private static string Names(IReadOnlyList<string> workers)
    {
        var names = string.Join(", ", workers.Take(MaxListedWorkers).Select(name => Truncate(Format.Sanitize(name))));
        var more = workers.Count > MaxListedWorkers ? $" and {workers.Count - MaxListedWorkers} more" : "";
        return names + more;
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
    }
}
