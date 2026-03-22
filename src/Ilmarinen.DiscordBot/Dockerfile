FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY src/Ilmarinen.Protocol/Ilmarinen.Protocol.csproj src/Ilmarinen.Protocol/
COPY src/Ilmarinen.NotificationClient/Ilmarinen.NotificationClient.csproj src/Ilmarinen.NotificationClient/
COPY src/Ilmarinen.DiscordBot/Ilmarinen.DiscordBot.csproj src/Ilmarinen.DiscordBot/
RUN dotnet restore src/Ilmarinen.DiscordBot/Ilmarinen.DiscordBot.csproj

# Copy source and build
COPY src/ src/
RUN dotnet publish src/Ilmarinen.DiscordBot/Ilmarinen.DiscordBot.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "ilmarinen-discord-bot.dll"]
