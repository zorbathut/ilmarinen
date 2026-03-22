namespace Ilmarinen.DiscordBot;

public class DiscordBotConfig
{
    public string BotToken { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? MentionRoleId { get; set; }
    public string ServerUrl { get; set; } = "";
    public string SubscriberName { get; set; } = "discord-bot";
}
