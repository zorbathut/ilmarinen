using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using NUlid;
using NUnit.Framework;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class RepositoryTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _repo = new TestGitRepository();
        _repo.AddFile("pipeline.csx", """
            Step("hello")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "Hello!");
                });
            """);
        _repo.Commit("Init");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task DeleteRepository_NoPipelines_Succeeds()
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "delete-me",
            RepoUrl = _repo.Url
        });

        var response = await _fixture.HttpClient.DeleteAsync($"/api/repositories/{repository.Id}");
        Assert.That((int)response.StatusCode, Is.EqualTo(204));

        var getResponse = await _fixture.HttpClient.GetAsync($"/api/repositories/{repository.Id}");
        Assert.That((int)getResponse.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task DeleteRepository_WithPipelines_Returns400()
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "has-pipelines",
            RepoUrl = _repo.Url
        });

        await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "blocking-pipeline",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var response = await _fixture.HttpClient.DeleteAsync($"/api/repositories/{repository.Id}");
        Assert.That((int)response.StatusCode, Is.EqualTo(400));

        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("pipeline"));

        // Verify the repository still exists
        var repo = await _fixture.GetRepositoryAsync(repository.Id);
        Assert.That(repo.Name, Is.EqualTo("has-pipelines"));
    }

    [Test]
    public async Task DeleteRepository_AfterPipelinesDeleted_Succeeds()
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "eventually-deletable",
            RepoUrl = _repo.Url
        });

        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "temp-pipeline",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        // Can't delete yet
        var blocked = await _fixture.HttpClient.DeleteAsync($"/api/repositories/{repository.Id}");
        Assert.That((int)blocked.StatusCode, Is.EqualTo(400));

        // Delete the pipeline first
        await _fixture.DeletePipelineAsync(pipeline.Id);

        // Now deletion succeeds
        var response = await _fixture.HttpClient.DeleteAsync($"/api/repositories/{repository.Id}");
        Assert.That((int)response.StatusCode, Is.EqualTo(204));
    }

    [Test]
    public async Task DeleteRepository_NonExistent_Returns404()
    {
        var fakeId = Ulid.NewUlid();
        var response = await _fixture.HttpClient.DeleteAsync($"/api/repositories/{fakeId}");
        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }
}
