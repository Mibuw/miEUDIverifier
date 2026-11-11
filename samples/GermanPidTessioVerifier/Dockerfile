# Builds the backend-free German PID verifier as a container, so it can run where the access
# certificate already is instead of the key having to travel to the application.
#
# Build from the repository root:
#   docker build -f samples/GermanPidTessioVerifier/Dockerfile -t germanpid-tessio-verifier:test .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY samples/GermanPidTessioVerifier/GermanPidTessioVerifier.csproj samples/GermanPidTessioVerifier/
RUN dotnet restore samples/GermanPidTessioVerifier/GermanPidTessioVerifier.csproj

COPY samples/GermanPidTessioVerifier/ samples/GermanPidTessioVerifier/
RUN dotnet publish samples/GermanPidTessioVerifier/GermanPidTessioVerifier.csproj \
        -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# The wallet reaches this through a reverse proxy or a tunnel, which terminates TLS.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# Not root: the container reads a private key, and nothing here needs to write anywhere.
USER $APP_UID

ENTRYPOINT ["dotnet", "GermanPidTessioVerifier.dll"]
