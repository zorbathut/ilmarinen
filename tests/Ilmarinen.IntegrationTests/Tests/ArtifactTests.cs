using System.Text;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using NUnit.Framework;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class ArtifactTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task SubmitJob_WithArtifact_ArtifactIsRetrievable()
    {
        // Arrange - create pipeline that saves an artifact
        using var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", """
            Step("create-artifact")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("echo 'Hello from artifact!' > /workspace/output.txt");
                    await ctx.SaveArtifact("output.txt", "test-artifact");
                });
            """);
        repo.Commit("Add artifact pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert - job succeeded
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));

        // Assert - artifact is listed
        var artifacts = await _fixture.GetArtifactsAsync(jobId);
        Assert.That(artifacts, Has.Count.EqualTo(1));
        Assert.That(artifacts[0].Name, Is.EqualTo("test-artifact"));
        Assert.That(artifacts[0].Size, Is.GreaterThan(0));
    }

    [Test]
    public async Task GetArtifact_Download_ReturnsCorrectContent()
    {
        // Arrange - create pipeline that saves an artifact with known content
        using var repo = new TestGitRepository();
        var expectedContent = "Integration test artifact content 12345";
        repo.AddFile("pipeline.csx", $$"""
            Step("create-artifact")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("echo -n '{{expectedContent}}' > /workspace/data.txt");
                    await ctx.SaveArtifact("data.txt");
                });
            """);
        repo.Commit("Add artifact pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));

        var artifacts = await _fixture.GetArtifactsAsync(jobId);
        Assert.That(artifacts, Has.Count.EqualTo(1));

        // Download the artifact
        var content = await _fixture.DownloadArtifactAsync(jobId, artifacts[0].Id);
        var contentString = Encoding.UTF8.GetString(content);

        // Assert
        Assert.That(contentString, Is.EqualTo(expectedContent));
    }

    [Test]
    public async Task SubmitJob_WithMultipleArtifacts_AllAreRetrievable()
    {
        // Arrange - create pipeline that saves multiple artifacts
        using var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", """
            Step("create-artifacts")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("echo 'File 1' > /workspace/file1.txt");
                    await ctx.Shell("echo 'File 2' > /workspace/file2.txt");
                    await ctx.Shell("echo 'File 3' > /workspace/file3.txt");

                    await ctx.SaveArtifact("file1.txt", "artifact-one");
                    await ctx.SaveArtifact("file2.txt", "artifact-two");
                    await ctx.SaveArtifact("file3.txt", "artifact-three");
                });
            """);
        repo.Commit("Add multi-artifact pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));

        var artifacts = await _fixture.GetArtifactsAsync(jobId);
        Assert.That(artifacts, Has.Count.EqualTo(3));

        var artifactNames = artifacts.Select(a => a.Name).ToList();
        Assert.That(artifactNames, Does.Contain("artifact-one"));
        Assert.That(artifactNames, Does.Contain("artifact-two"));
        Assert.That(artifactNames, Does.Contain("artifact-three"));
    }
}
