FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
WORKDIR /src

# Copy csproj files and restore (worker csprojs included: the worker bundle is baked into this image)
COPY src/Ilmarinen.Core/Ilmarinen.Core.csproj src/Ilmarinen.Core/
COPY src/Ilmarinen.Protocol/Ilmarinen.Protocol.csproj src/Ilmarinen.Protocol/
COPY src/Ilmarinen.Database/Ilmarinen.Database.csproj src/Ilmarinen.Database/
COPY src/Ilmarinen.Server/Ilmarinen.Server.csproj src/Ilmarinen.Server/
COPY src/Ilmarinen.Scripting/Ilmarinen.Scripting.csproj src/Ilmarinen.Scripting/
COPY src/Ilmarinen.Docker/Ilmarinen.Docker.csproj src/Ilmarinen.Docker/
COPY src/Ilmarinen.Worker/Ilmarinen.Worker.csproj src/Ilmarinen.Worker/
RUN dotnet restore src/Ilmarinen.Server/Ilmarinen.Server.csproj \
    && dotnet restore src/Ilmarinen.Worker/Ilmarinen.Worker.csproj

# Copy source and build
COPY src/ src/
RUN dotnet publish src/Ilmarinen.Server/Ilmarinen.Server.csproj -c Release -o /app

# Publish and zip the worker bundle served to launcher-run workers. Built from the same source tree as the server, so bundle and server are protocol-compatible by construction. Same portable (framework-dependent, RID-less) layout the worker image runs. Neither the publish nor the zip is deterministic, so any rebuild that misses the layer cache yields a new bundle hash and one swap per launcher worker on the next deploy — accepted cost, not a bug.
RUN dotnet publish src/Ilmarinen.Worker/Ilmarinen.Worker.csproj -c Release -o /worker-publish \
    && cd /worker-publish && zip -qr /worker-bundle.zip .

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=build /worker-bundle.zip /app/worker-bundle.zip

ENV ASPNETCORE_URLS=http://+:8080
ENV Server__WorkerBundlePath=/app/worker-bundle.zip
EXPOSE 8080

ENTRYPOINT ["dotnet", "ilmarinen-server.dll"]
