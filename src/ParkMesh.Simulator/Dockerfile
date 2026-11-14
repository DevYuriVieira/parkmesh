FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ParkMesh.Domain/ParkMesh.Domain.csproj src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ParkMesh.Application.csproj src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ParkMesh.Infrastructure.csproj src/ParkMesh.Infrastructure/
COPY src/ParkMesh.Simulator/ParkMesh.Simulator.csproj src/ParkMesh.Simulator/

RUN dotnet restore src/ParkMesh.Simulator/ParkMesh.Simulator.csproj

COPY src/ParkMesh.Domain/ src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ src/ParkMesh.Infrastructure/
COPY src/ParkMesh.Simulator/ src/ParkMesh.Simulator/

RUN dotnet publish src/ParkMesh.Simulator/ParkMesh.Simulator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "ParkMesh.Simulator.dll"]
