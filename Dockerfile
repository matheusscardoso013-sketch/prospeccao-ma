# Build multi-stage do ProspeccaoMA.Web (ASP.NET Core 8) para Render/Railway.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (cacheia a camada de pacotes)
COPY src/ProspeccaoMA.Web/ProspeccaoMA.Web.csproj ./ProspeccaoMA.Web/
RUN dotnet restore ./ProspeccaoMA.Web/ProspeccaoMA.Web.csproj

# Publish
COPY src/ProspeccaoMA.Web/ ./ProspeccaoMA.Web/
RUN dotnet publish ./ProspeccaoMA.Web/ProspeccaoMA.Web.csproj -c Release -o /app /p:UseAppHost=false

# Runtime enxuto
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
# A plataforma define a env PORT; o app a lê em Program.cs.
ENTRYPOINT ["dotnet", "ProspeccaoMA.Web.dll"]
