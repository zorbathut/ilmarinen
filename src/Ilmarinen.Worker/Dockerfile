FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY src/Ilmarinen.Protocol/Ilmarinen.Protocol.csproj src/Ilmarinen.Protocol/
COPY src/Ilmarinen.Core/Ilmarinen.Core.csproj src/Ilmarinen.Core/
COPY src/Ilmarinen.Scripting/Ilmarinen.Scripting.csproj src/Ilmarinen.Scripting/
COPY src/Ilmarinen.Docker/Ilmarinen.Docker.csproj src/Ilmarinen.Docker/
COPY src/Ilmarinen.Worker/Ilmarinen.Worker.csproj src/Ilmarinen.Worker/
RUN dotnet restore src/Ilmarinen.Worker/Ilmarinen.Worker.csproj

# Copy source and build
COPY src/ src/
RUN dotnet publish src/Ilmarinen.Worker/Ilmarinen.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0

# Install Docker CLI (worker needs to spawn containers)
RUN apt-get update && apt-get install -y \
    ca-certificates \
    curl \
    gnupg \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg \
    && chmod a+r /etc/apt/keyrings/docker.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian bookworm stable" > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y docker-ce-cli \
    && rm -rf /var/lib/apt/lists/*

# Install git (needed by LibGit2Sharp)
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "ilmarinen-worker.dll"]
