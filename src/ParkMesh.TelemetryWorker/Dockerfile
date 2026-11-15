FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ParkMesh.Domain/ParkMesh.Domain.csproj src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ParkMesh.Application.csproj src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ParkMesh.Infrastructure.csproj src/ParkMesh.Infrastructure/
COPY src/ParkMesh.TelemetryWorker/ParkMesh.TelemetryWorker.csproj src/ParkMesh.TelemetryWorker/

RUN dotnet restore src/ParkMesh.TelemetryWorker/ParkMesh.TelemetryWorker.csproj

COPY src/ParkMesh.Domain/ src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ src/ParkMesh.Infrastructure/
COPY src/ParkMesh.TelemetryWorker/ src/ParkMesh.TelemetryWorker/

RUN dotnet publish src/ParkMesh.TelemetryWorker/ParkMesh.TelemetryWorker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
USER app

ENTRYPOINT ["dotnet", "ParkMesh.TelemetryWorker.dll"]
