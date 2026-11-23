FROM mcr.microsoft.com/dotnet/sdk:10.0.201-noble@sha256:127d7d4d601ae26b8e04c54efb37e9ce8766931bded0ee59fcd799afd21d6850 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore src/Guardian.Cli --locked-mode \
    && dotnet publish src/Guardian.Cli -c Release --no-restore -o /publish
COPY scripts/install-oasdiff.ps1 /tmp/install-oasdiff.ps1
RUN pwsh -NoProfile -File /tmp/install-oasdiff.ps1 -Destination /engine

FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble@sha256:64a538092439382d055c87c0f49092efb8bca883c736c54ae523da7fd8e02d18
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git \
    && rm -rf /var/lib/apt/lists/* \
    && printf '%s\n' '#!/bin/sh' 'exec dotnet /app/guardian.dll "$@"' > /usr/local/bin/guardian \
    && chmod 0755 /usr/local/bin/guardian
COPY --from=build /publish/ /app/
COPY --from=build /engine/oasdiff /usr/local/bin/oasdiff
ARG GUARDIAN_SOURCE_SHA
LABEL org.opencontainers.image.revision=$GUARDIAN_SOURCE_SHA
ENV GUARDIAN_OASDIFF_PATH=/usr/local/bin/oasdiff
USER $APP_UID
ENTRYPOINT ["guardian"]
