using Ilmarinen.Docker;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Scripting;
using LibGit2Sharp;
using Microsoft.AspNetCore.SignalR.Client;

namespace Ilmarinen.Worker.Services;

public class JobRunner
{
    private readonly WorkerConfig _config;
    private readonly JobAssignment _job;
    private readonly HubConnection _connection;
    private readonly ILogger _logger;

    public JobRunner(
        WorkerConfig config,
        JobAssignment job,
        HubConnection connection,
        ILogger logger)
    {
        _config = config;
        _job = job;
        _connection = connection;
        _logger = logger;
    }

    public async Task<JobCompleted> ExecuteAsync()
    {
        var workDir = Path.Combine(_config.WorkspacePath, _job.Id.ToString());

        try
        {
            // 1. Clone repository
            _logger.LogInformation("Cloning {RepoUrl}...", _job.RepoUrl);
            CloneRepository(workDir);

            // 2. Checkout ref
            _logger.LogInformation("Checking out {Ref}...", _job.Ref);
            CheckoutRef(workDir);

            // 3. Load pipeline script
            var scriptPath = Path.Combine(workDir, _job.ScriptPath);
            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Pipeline script not found: {ScriptPath}", _job.ScriptPath);
                return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
            }

            _logger.LogInformation("Loading pipeline: {ScriptPath}", _job.ScriptPath);
            var steps = await PipelineScript.LoadAsync(scriptPath);

            if (steps.Count == 0)
            {
                _logger.LogError("Pipeline has no steps");
                return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
            }

            // 4. Run pipeline
            _logger.LogInformation("Running {StepCount} step(s)...", steps.Count);

            var runner = new PipelineRunner(workDir);
            var success = await runner.RunAsync(steps);

            return new JobCompleted
            {
                Id = _job.Id,
                Status = success ? JobStatus.Success : JobStatus.Failed
            };
        }
        finally
        {
            // Cleanup
            try
            {
                if (Directory.Exists(workDir))
                {
                    SetAttributesNormal(new DirectoryInfo(workDir));
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup work directory");
            }
        }
    }

    private void CloneRepository(string workDir)
    {
        Repository.Clone(_job.RepoUrl, workDir);
    }

    private void CheckoutRef(string workDir)
    {
        using var repo = new Repository(workDir);

        var target = repo.Lookup(_job.Ref);
        if (target != null)
        {
            Commands.Checkout(repo, target as Commit ?? ((GitObject)target).Peel<Commit>());
        }
        else
        {
            var remoteBranch = repo.Branches[$"origin/{_job.Ref}"];
            if (remoteBranch != null)
            {
                Commands.Checkout(repo, remoteBranch.Tip);
            }
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
