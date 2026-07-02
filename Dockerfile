# ──────────────────────────────────────────────────────────────────────────────
# EUDI Wallet Verifier – Multi-Stage Container Build
# Baut die ASP.NET-Core-Web-App und startet sie im schlanken Runtime-Image.
# ──────────────────────────────────────────────────────────────────────────────

# ── 1. Build-Stage: SDK zum Restore + Publish ────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Nur die Projektdateien kopieren → NuGet-Restore wird als Layer gecacht
COPY src/miEUDIverifier/miEUDIverifier.csproj             src/miEUDIverifier/
COPY src/miEUDIverifier.Core/miEUDIverifier.Core.csproj   src/miEUDIverifier.Core/
RUN dotnet restore src/miEUDIverifier/miEUDIverifier.csproj

# Restlichen Quellcode kopieren und veröffentlichen
COPY . .
RUN dotnet publish src/miEUDIverifier/miEUDIverifier.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ── 2. Runtime-Stage: nur die ASP.NET-Runtime ────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Kestrel bindet laut appsettings.json auf http://0.0.0.0:5050
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5050

ENTRYPOINT ["dotnet", "miEUDIverifier.dll"]
