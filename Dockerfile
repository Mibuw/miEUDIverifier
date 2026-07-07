# ──────────────────────────────────────────────────────────────────────────────
# EUDI Wallet Verifier – multi-stage container build
# Builds the ASP.NET Core web app and runs it in the slim runtime image.
# ──────────────────────────────────────────────────────────────────────────────

# ── 1. Build stage: SDK for restore + publish ────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project files first → the NuGet restore gets cached as a layer
COPY src/miEUDIverifier/miEUDIverifier.csproj             src/miEUDIverifier/
COPY src/miEUDIverifier.Core/miEUDIverifier.Core.csproj   src/miEUDIverifier.Core/
RUN dotnet restore src/miEUDIverifier/miEUDIverifier.csproj

# Copy the remaining source code and publish
COPY . .
RUN dotnet publish src/miEUDIverifier/miEUDIverifier.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ── 2. Runtime stage: ASP.NET runtime only ───────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Kestrel binds to http://0.0.0.0:5050 as configured in appsettings.json
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5050

ENTRYPOINT ["dotnet", "miEUDIverifier.dll"]
