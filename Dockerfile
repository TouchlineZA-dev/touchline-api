# touchline-api for Railway (and any container host).
# CI builds this file on every PR (job "docker"), so a missing COPY source fails CI, not the deploy.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props sets TargetFramework, Nullable and ImplicitUsings for every project: without it nothing builds.
COPY Directory.Build.props ./
COPY src/Touchline.Api/Touchline.Api.csproj src/Touchline.Api/
RUN dotnet restore src/Touchline.Api/Touchline.Api.csproj

COPY src/Touchline.Api/ src/Touchline.Api/
RUN dotnet publish src/Touchline.Api/Touchline.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The app listens on $PORT when the host sets it, otherwise 8080 (Program.cs). Railway's domain must target the same port.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Touchline.Api.dll"]
