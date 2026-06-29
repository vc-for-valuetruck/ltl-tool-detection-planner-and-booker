# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MyApp.Api/MyApp.Api.csproj src/MyApp.Api/
RUN dotnet restore src/MyApp.Api/MyApp.Api.csproj
COPY src/MyApp.Api/ src/MyApp.Api/
RUN dotnet publish src/MyApp.Api/MyApp.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
