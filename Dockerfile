# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props OpenFno.Broker.slnx ./
COPY src/ src/
RUN dotnet publish src/OpenFno.Broker.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY data/calendar /data/calendar
COPY data/reference /data/reference
# A named volume mounted here takes this ownership when it is first created.
RUN mkdir -p /data/keys /data/instruments && chown app:app /data/keys
ENV ASPNETCORE_URLS=http://+:8080 \
    Broker__DataDirectory=/data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "OpenFno.Broker.Api.dll"]
