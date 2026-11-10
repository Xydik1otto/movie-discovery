# Multi-stage: SDK 8.0 для збірки → aspnet:8.0 для рантайму (PLAN.md, порт 5200)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копіюємо csproj окремо для кешування restore-шару
COPY ["src/MovieDiscovery.Core/MovieDiscovery.Core.csproj", "src/MovieDiscovery.Core/"]
COPY ["src/MovieDiscovery.Infrastructure/MovieDiscovery.Infrastructure.csproj", "src/MovieDiscovery.Infrastructure/"]
COPY ["src/MovieDiscovery.Api/MovieDiscovery.Api.csproj", "src/MovieDiscovery.Api/"]
RUN dotnet restore "src/MovieDiscovery.Api/MovieDiscovery.Api.csproj"

# Копіюємо решту і публікуємо Release
COPY src/ src/
RUN dotnet publish "src/MovieDiscovery.Api/MovieDiscovery.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5200
EXPOSE 5200

ENTRYPOINT ["dotnet", "MovieDiscovery.Api.dll"]
