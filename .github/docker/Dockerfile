# Fork image: the linuxserver image of the same upstream version with the app binaries replaced.
# Build context: the _artifacts directory of build.sh --packages.
ARG BASE_VERSION

FROM scratch AS bin-amd64
COPY linux-musl-x64/net6.0/Sonarr /

FROM scratch AS bin-arm64
COPY linux-musl-arm64/net6.0/Sonarr /

FROM bin-${TARGETARCH} AS bin

FROM lscr.io/linuxserver/sonarr:${BASE_VERSION}
RUN rm -rf /app/sonarr/bin
COPY --from=bin --chown=1001:1001 / /app/sonarr/bin/
