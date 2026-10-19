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

# appsettings.json binds to loopback only, which is the safe default for a local run.
# A container has to be reachable from the reverse proxy, so the binding is widened here.
# Kestrel endpoints take precedence over ASPNETCORE_URLS, so this is the knob that works.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Kestrel__Endpoints__Http__Url=http://0.0.0.0:5050
EXPOSE 5050

ENTRYPOINT ["dotnet", "miEUDIverifier.dll"]
