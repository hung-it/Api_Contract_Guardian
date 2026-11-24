FROM mcr.microsoft.com/dotnet/sdk:10.0.201-noble@sha256:127d7d4d601ae26b8e04c54efb37e9ce8766931bded0ee59fcd799afd21d6850 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY schemas/report-v1.schema.json schemas/
COPY src/Guardian.Api/ src/Guardian.Api/
RUN dotnet restore src/Guardian.Api --locked-mode --disable-build-servers \
    && dotnet publish src/Guardian.Api -c Release --no-restore --disable-build-servers -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21
WORKDIR /app
RUN mkdir /keys && chown "$APP_UID" /keys
COPY --from=build /publish/ ./
USER $APP_UID
CMD ["dotnet", "Guardian.Api.dll"]
