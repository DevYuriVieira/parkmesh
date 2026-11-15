FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/ParkMesh.Domain/ParkMesh.Domain.csproj src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ParkMesh.Application.csproj src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ParkMesh.Infrastructure.csproj src/ParkMesh.Infrastructure/
COPY src/ParkMesh.Api/ParkMesh.Api.csproj src/ParkMesh.Api/

RUN dotnet restore src/ParkMesh.Api/ParkMesh.Api.csproj

COPY src/ParkMesh.Domain/ src/ParkMesh.Domain/
COPY src/ParkMesh.Application/ src/ParkMesh.Application/
COPY src/ParkMesh.Infrastructure/ src/ParkMesh.Infrastructure/
COPY src/ParkMesh.Api/ src/ParkMesh.Api/

RUN dotnet publish src/ParkMesh.Api/ParkMesh.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
COPY web/ ./web/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "ParkMesh.Api.dll"]
