using Discord.WebSocket;
using Discord;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.DiscordBot;

public class DiscordNotificationService : BackgroundService, INotificationHandler
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private DiscordSocketClient? _discordClient;
    private IlmarinenNotificationClient? _notificationClient;
    private DiscordBotConfig? _config;
    private ulong _channelId;
    private bool _isReady;

    public DiscordNotificationService(
        IConfiguration configuration,
        ILogger<DiscordNotificationService> logger,
        IHostApplicationLifetime lifetime)
    {
        _configuration = configuration;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!TryLoadConfig())
            {
                _logger.LogInformation("Discord bot not configured. Exiting gracefully.");
                _lifetime.StopApplication();
                return;
            }

            await InitializeDiscordAsync(stoppingToken);

            var serverUrl = Environment.GetEnvironmentVariable("ILMARINEN_SERVER_URL")
                ?? _config!.ServerUrl;
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                throw new InvalidOperationException(
                    "Server URL not configured. Set ILMARINEN_SERVER_URL or serverUrl in config.");
            }

            _notificationClient = new IlmarinenNotificationClient(serverUrl);
            var processor = new NotificationProcessor(
                _notificationClient,
                this,
                new NotificationProcessorOptions { SubscriberName = _config!.SubscriberName },
                _logger);
            await processor.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Discord notification service was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in Discord notification service");
            throw;
        }
    }

    private bool TryLoadConfig()
    {
        var configPath = _configuration["ConfigPath"] ?? "config/discord-bot.json";

        if (!File.Exists(configPath))
        {
            _logger.LogWarning(
                "Discord bot config file not found at {ConfigPath}. " +
                "Create config/discord-bot.json with botToken, channelId, and serverUrl.",
                configPath);
            return false;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<DiscordBotConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (_config == null)
            {
                _logger.LogError("Failed to deserialize config from {ConfigPath}", configPath);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_config.BotToken) || _config.BotToken == "YOUR_BOT_TOKEN_HERE")
            {
                _logger.LogError("Invalid botToken in config file.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_config.ChannelId) || !ulong.TryParse(_config.ChannelId, out _channelId))
            {
                _logger.LogError("Invalid channelId in config file. Must be a numeric Discord channel ID.");
                return false;
            }

            _logger.LogInformation("Loaded Discord bot configuration from {ConfigPath}", configPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading config from {ConfigPath}", configPath);
            return false;
        }
    }

    private async Task InitializeDiscordAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Initializing Discord client...");

        _discordClient = new DiscordSocketClient(new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            GatewayIntents = GatewayIntents.Guilds
        });

        _discordClient.Log += LogDiscordMessage;
        _discordClient.Ready += () =>
        {
            _isReady = true;
            _logger.LogInformation("Discord client ready");
            return Task.CompletedTask;
        };

        await _discordClient.LoginAsync(TokenType.Bot, _config!.BotToken);
        await _discordClient.StartAsync();

        var timeout = TimeSpan.FromSeconds(30);
        var elapsed = TimeSpan.Zero;
        while (!_isReady && elapsed < timeout && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
            elapsed += TimeSpan.FromMilliseconds(100);
        }

        if (!_isReady)
            throw new TimeoutException("Discord client failed to become ready within 30 seconds");

        _logger.LogInformation("Discord client initialized successfully");
    }

    public async Task<bool> HandleAsync(JobNotification notification, CancellationToken ct)
    {
        await SendToDiscordAsync(notification);
        return true;
    }

    private async Task SendToDiscordAsync(JobNotification notification)
    {
        var channel = await _discordClient!.GetChannelAsync(_channelId) as IMessageChannel
            ?? throw new InvalidOperationException($"Cannot find Discord channel with ID {_channelId}");

        var isSuccess = notification.Status == JobStatus.Success;
        var color = isSuccess ? Color.Green : Color.Red;
        var title = isSuccess ? "Build Succeeded" : "Build Failed";
        var baseUrl = _config!.PublicUrl ?? _config.ServerUrl;
        string? jobUrl = null;
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.IsWellFormedUriString(baseUrl.TrimEnd('/'), UriKind.Absolute))
        {
            jobUrl = $"{baseUrl.TrimEnd('/')}/jobs/{notification.JobId}";
        }
        else
        {
            _logger.LogWarning("Cannot generate job URL: base URL '{BaseUrl}' is not a valid absolute URI. Set PublicUrl in config to a valid URL", baseUrl);
        }

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("Repository", notification.RepoUrl, inline: true)
            .AddField("Branch", notification.Ref, inline: true)
            .AddField("Script", notification.ScriptPath, inline: true);

        if (jobUrl != null)
            embed.WithUrl(jobUrl);

        if (notification.StartedAt != null && notification.CompletedAt != null)
        {
            var duration = notification.CompletedAt.Value - notification.StartedAt.Value;
            embed.AddField("Duration", FormatDuration(duration), inline: true);
        }

        if (notification.PipelineName != null)
            embed.AddField("Pipeline", notification.PipelineName, inline: true);

        embed.WithFooter($"Job ID: {notification.JobId}");

        string? messageContent = null;
        if (!string.IsNullOrWhiteSpace(_config!.MentionRoleId) && !isSuccess)
            messageContent = $"<@&{_config.MentionRoleId}>";

        await channel.SendMessageAsync(text: messageContent, embed: embed.Build());

        _logger.LogInformation("Sent {Status} notification to Discord for job {JobId}",
            notification.Status, notification.JobId);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return $"{duration.Seconds}s";
        if (duration.TotalHours < 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    private Task LogDiscordMessage(LogMessage message)
    {
        var logLevel = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, message.Exception, "[Discord] {Message}", message.Message);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Discord notification service...");

        if (_discordClient != null)
        {
            await _discordClient.StopAsync();
            await _discordClient.DisposeAsync();
        }

        _notificationClient?.Dispose();

        await base.StopAsync(cancellationToken);
    }
}
