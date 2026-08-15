FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/Ilmarinen.WorkerLauncher/Ilmarinen.WorkerLauncher.csproj src/Ilmarinen.WorkerLauncher/
RUN dotnet restore src/Ilmarinen.WorkerLauncher/Ilmarinen.WorkerLauncher.csproj

COPY src/Ilmarinen.WorkerLauncher/ src/Ilmarinen.WorkerLauncher/
RUN dotnet publish src/Ilmarinen.WorkerLauncher/Ilmarinen.WorkerLauncher.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0

# The worker child process runs inside this container, so it needs everything the worker image has: the Docker CLI to spawn job containers, and git for LibGit2Sharp.
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

RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "ilmarinen-worker-launcher.dll"]
