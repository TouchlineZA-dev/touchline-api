FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Touchline.Api/Touchline.Api.csproj src/Touchline.Api/
RUN dotnet restore src/Touchline.Api/Touchline.Api.csproj

COPY src/Touchline.Api/ src/Touchline.Api/
RUN dotnet publish src/Touchline.Api/Touchline.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet Touchline.Api.dll"]
