# Stack Inspection API — image Linux x64 (dipakai Coolify / server dev).
# Model TIDAK ikut di image: pasang folder model lewat volume ke /app/models
# (satu subfolder per model: <nama>/<nama>.onnx + model-card.json).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY Directory.Build.props Directory.Build.targets StackInspection.sln ./
COPY src/ src/
COPY fixtures/ fixtures/
# -p:RuntimeIdentifier wajib: tanpa itu `restore -r` terpisah tidak memenuhi kondisi RuntimeIdentifier di csproj,
# sehingga paket native (OpenCV, PaddleInference) tidak ikut dan API gagal memuat paddle_inference_c.
RUN dotnet restore src/StackInspection.Api/StackInspection.Api.csproj -r linux-x64 -p:RuntimeIdentifier=linux-x64
RUN dotnet publish src/StackInspection.Api/StackInspection.Api.csproj -c Release -r linux-x64 \
    --self-contained false --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# libgomp1: dibutuhkan oneDNN (runtime Paddle MKL). curl: health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/models && chown app:app /app/models
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Vision__ModelsDirectory=/app/models
EXPOSE 8080
VOLUME /app/models
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "StackInspection.Api.dll"]
