# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/LtlTool.Api/LtlTool.Api.csproj src/LtlTool.Api/
RUN dotnet restore src/LtlTool.Api/LtlTool.Api.csproj
COPY src/LtlTool.Api/ src/LtlTool.Api/
RUN dotnet publish src/LtlTool.Api/LtlTool.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LtlTool.Api.dll"]
