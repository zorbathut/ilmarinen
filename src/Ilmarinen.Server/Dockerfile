FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY src/Ilmarinen.Protocol/Ilmarinen.Protocol.csproj src/Ilmarinen.Protocol/
COPY src/Ilmarinen.Database/Ilmarinen.Database.csproj src/Ilmarinen.Database/
COPY src/Ilmarinen.Server/Ilmarinen.Server.csproj src/Ilmarinen.Server/
RUN dotnet restore src/Ilmarinen.Server/Ilmarinen.Server.csproj

# Copy source and build
COPY src/Ilmarinen.Protocol/ src/Ilmarinen.Protocol/
COPY src/Ilmarinen.Database/ src/Ilmarinen.Database/
COPY src/Ilmarinen.Server/ src/Ilmarinen.Server/
RUN dotnet publish src/Ilmarinen.Server/Ilmarinen.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ilmarinen-server.dll"]
