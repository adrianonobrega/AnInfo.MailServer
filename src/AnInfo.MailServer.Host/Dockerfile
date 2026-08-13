FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["NuGet.config", "global.json", "Directory.Build.props", "AnInfo.MailServer.sln", "./"]
COPY ["src/AnInfo.MailServer.Domain/AnInfo.MailServer.Domain.csproj", "src/AnInfo.MailServer.Domain/"]
COPY ["src/AnInfo.MailServer.Application/AnInfo.MailServer.Application.csproj", "src/AnInfo.MailServer.Application/"]
COPY ["src/AnInfo.MailServer.Infrastructure/AnInfo.MailServer.Infrastructure.csproj", "src/AnInfo.MailServer.Infrastructure/"]
COPY ["src/AnInfo.MailServer.Host/AnInfo.MailServer.Host.csproj", "src/AnInfo.MailServer.Host/"]
COPY ["src/AnInfo.MailServer.TestClient/AnInfo.MailServer.TestClient.csproj", "src/AnInfo.MailServer.TestClient/"]
COPY ["tests/AnInfo.MailServer.Tests/AnInfo.MailServer.Tests.csproj", "tests/AnInfo.MailServer.Tests/"]
RUN dotnet restore AnInfo.MailServer.sln

COPY . .
RUN dotnet publish src/AnInfo.MailServer.Host/AnInfo.MailServer.Host.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/logs && chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 2525 8085
ENTRYPOINT ["dotnet", "AnInfo.MailServer.Host.dll"]
