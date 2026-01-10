// Jenkins Migration Example
//
// This shows the original Jenkinsfile and its Conductor equivalent side-by-side.
//
// ┌────────────────────────────────────────────────────────────────────────────┐
// │ ORIGINAL JENKINSFILE                                                       │
// ├────────────────────────────────────────────────────────────────────────────┤
// │ pipeline {                                                                 │
// │     agent any                                                              │
// │                                                                            │
// │     stages {                                                               │
// │         stage('Build') {                                                   │
// │             steps {                                                        │
// │                 script {                                                   │
// │                     def docker = load 'build/util.groovy'                  │
// │                     docker.run(                                            │
// │                         "./tool.bat deploy_full"                           │
// │                     )                                                      │
// │                                                                            │
// │                     archiveArtifacts artifacts: "deploy/*.zip",            │
// │                                        fingerprint: true                   │
// │                 }                                                          │
// │             }                                                              │
// │         }                                                                  │
// │     }                                                                      │
// │     post {                                                                 │
// │         failure {                                                          │
// │             withCredentials([string(credentialsId: 'discord-webhook',      │
// │                                     variable: 'DISCORD_WEBHOOK')]) {       │
// │                 discordSend(                                               │
// │                     description: "Build failed: ${env.JOB_NAME} " +        │
// │                                  "#${env.BUILD_NUMBER}",                   │
// │                     link: env.BUILD_URL,                                   │
// │                     result: currentBuild.currentResult,                    │
// │                     title: "Build Failed",                                 │
// │                     webhookURL: DISCORD_WEBHOOK                            │
// │                 )                                                          │
// │             }                                                              │
// │         }                                                                  │
// │     }                                                                      │
// │ }                                                                          │
// └────────────────────────────────────────────────────────────────────────────┘
//
// Key differences:
// 1. "agent any" → explicit container image (required in Conductor)
// 2. Groovy scripting → native C#
// 3. archiveArtifacts → ctx.Output()
// 4. withCredentials → ctx.Secret() (cleaner injection)
// 5. post { failure { } } → OnFailure() handler

#r "Conductor"

Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")  // Or whatever image has your build tools
    .Run(async ctx =>
    {
        // Replaces: docker.run("./tool.bat deploy_full")
        // In Conductor, you're already IN a container, so just run the command
        await ctx.Exec("./tool.bat", "deploy_full");
        
        // Replaces: archiveArtifacts artifacts: "deploy/*.zip", fingerprint: true
        ctx.Output("deploy", "deploy/*.zip", fingerprint: true);
    });

// Replaces: post { failure { ... } }
OnFailure(async ctx =>
{
    // Replaces: withCredentials([string(credentialsId: 'discord-webhook', ...)])
    var webhook = ctx.Secret("discord-webhook");
    
    // Replaces: discordSend(...)
    await ctx.Discord(webhook).Send(new DiscordMessage
    {
        Title = "Build Failed",
        Description = $"Build failed: {ctx.JobName} #{ctx.BuildNumber}",
        Link = ctx.BuildUrl,
        Color = DiscordColor.Danger
    });
});


// ============================================================================
// EXPANDED VERSION
// ============================================================================
// If you want to break it into more stages (like the Jenkins version implies),
// here's a more expanded version:

/*
var buildOutput = Step("build")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Run(async ctx =>
    {
        await ctx.Exec("dotnet", "build", "-c", "Release");
        await ctx.Exec("dotnet", "publish", "-c", "Release", "-o", "publish/");
    });

var deployPackage = Step("package")
    .Image("mcr.microsoft.com/dotnet/sdk:8.0")
    .Needs("build")
    .Run(async ctx =>
    {
        await ctx.Exec("./tool.bat", "deploy_full");
        ctx.Output("deploy", "deploy/*.zip", fingerprint: true);
    });

Step("deploy")
    .Image("your-deploy-image:latest")
    .Needs("package")
    .When(ctx => ctx.Branch == "main")
    .Run(async ctx =>
    {
        ctx.Input("deploy");
        await ctx.Exec("./deploy.sh");
    });

OnFailure(async ctx =>
{
    var webhook = ctx.Secret("discord-webhook");
    await ctx.Discord(webhook).Send(new DiscordMessage
    {
        Title = "Build Failed",
        Description = $"Build failed: {ctx.JobName} #{ctx.BuildNumber}",
        Link = ctx.BuildUrl,
        Color = DiscordColor.Danger
    });
});
*/
