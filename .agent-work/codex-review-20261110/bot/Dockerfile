# multi-stage: sdk 8.0 → runtime 8.0 (контекст збірки — каталог bot/)

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# спершу тільки csproj — шар з restore кешується
COPY Bot.csproj ./
RUN dotnet restore Bot.csproj

COPY . .
RUN dotnet publish Bot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

# docker CLI в runtime-образі (debian): боту потрібен лише `docker restart moviediscovery-tunnel`
# через маунт /var/run/docker.sock (див. docker-compose.yml, сервіс tunnel).
RUN apt-get update && apt-get install -y --no-install-recommends docker.io \
    && rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "Bot.dll"]
