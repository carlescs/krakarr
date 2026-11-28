# syntax=docker/dockerfile:1

ARG VERSION=0.0.0-dev

# ---- 1. Angular frontend (platform independent output, so build it natively) ---------------
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# Builds into ../src/Krakarr.Host/wwwroot (see web/angular.json).
RUN npx ng build --configuration production

# ---- 2. .NET backend ----------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
ARG VERSION
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
# The web UI is built in the Node stage, so skip the MSBuild web build (no Node in this image).
RUN dotnet restore src/Krakarr.Host/Krakarr.Host.csproj -a $TARGETARCH -p:SkipWebBuild=true
RUN dotnet publish src/Krakarr.Host/Krakarr.Host.csproj -c Release -a $TARGETARCH --no-restore -o /app \
    -p:SkipWebBuild=true -p:UseAppHost=false -p:DebugType=none -p:Version=$VERSION
COPY --from=web /src/src/Krakarr.Host/wwwroot /app/wwwroot

# ---- 3. Subtitle sync tools ----------------------------------------------------------------
# alass, plus an ffmpeg/ffprobe built with only what syncing needs (demuxing, audio decoding, text subtitle tracks):
# a few MB instead of the hundreds a full ffmpeg brings. The video parsers (no decoders) let ffprobe report the size and
# pixel format of containers whose headers don't carry them (MPEG-TS, AVI) for media info. Cross-compiled natively for the target, on the runtime's
# Ubuntu release so the glibc matches.
FROM --platform=$BUILDPLATFORM ubuntu:26.04 AS tools
ARG TARGETARCH
ARG FFMPEG_VERSION=9.0.2
ARG FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
ARG FFMPEG_URL=https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz
ARG ALASS_VERSION=2.0.0
ARG RUST_VERSION=1.85.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl xz-utils make nasm gcc libc6-dev \
       gcc-x86-64-linux-gnu libc6-dev-amd64-cross gcc-aarch64-linux-gnu libc6-dev-arm64-cross \
    && rm -rf /var/lib/apt/lists/*
RUN case "$TARGETARCH" in \
      amd64) echo "x86_64 x86_64-linux-gnu x86_64-unknown-linux-gnu" ;; \
      arm64) echo "aarch64 aarch64-linux-gnu aarch64-unknown-linux-gnu" ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /target
RUN read arch triple _ < /target \
    && curl -fsSL "$FFMPEG_URL" -o /tmp/ffmpeg.tar.xz \
    && echo "$FFMPEG_SHA256  /tmp/ffmpeg.tar.xz" | sha256sum -c - \
    && mkdir /tmp/ffmpeg && tar -xJf /tmp/ffmpeg.tar.xz -C /tmp/ffmpeg --strip-components=1 \
    && cd /tmp/ffmpeg \
    && ./configure --prefix=/out --enable-cross-compile --arch=$arch --target-os=linux --cross-prefix=$triple- \
       --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug --disable-ffplay \
       --enable-protocol=file,pipe \
       --enable-demuxer=matroska,mov,avi,mpegts,mpegps,asf,flv,ogg,wav \
       --enable-parser=aac,aac_latm,ac3,dca,flac,mlp,mpegaudio,opus,vorbis,h264,hevc,mpegvideo,mpeg4video \
       --enable-decoder=aac,aac_latm,ac3,eac3,dca,truehd,mlp,mp2,mp3,opus,vorbis,flac,alac,wmav2,wmapro \
       --enable-decoder=pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_s16be,pcm_bluray,pcm_dvd \
       --enable-decoder=ass,ssa,subrip,srt,webvtt,movtext,text \
       --enable-encoder=pcm_s16le,subrip,srt \
       --enable-muxer=pcm_s16le,wav,srt \
       --enable-filter=aresample,aformat,anull,abuffer,abuffersink \
       > /tmp/configure.log 2>&1 || { tail -n 30 /tmp/configure.log; exit 1; }; \
    if grep "did not match anything" /tmp/configure.log; then exit 1; fi \
    && make -j"$(nproc)" && make install \
    && rm -rf /tmp/ffmpeg /tmp/ffmpeg.tar.xz
ENV RUSTUP_HOME=/opt/rustup CARGO_HOME=/opt/cargo PATH=/opt/cargo/bin:$PATH \
    CARGO_PROFILE_RELEASE_STRIP=symbols \
    CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER=x86_64-linux-gnu-gcc \
    CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
RUN read _ _ target < /target \
    && curl -fsSL "https://static.rust-lang.org/rustup/dist/$(uname -m)-unknown-linux-gnu/rustup-init" -o /tmp/rustup-init \
    && chmod +x /tmp/rustup-init \
    && /tmp/rustup-init -y --profile minimal --default-toolchain $RUST_VERSION --target $target --no-modify-path \
    && cargo install alass-cli --version $ALASS_VERSION --locked --target $target --root /out \
    && rm -rf /tmp/rustup-init $CARGO_HOME/registry

# ---- 4. Runtime ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0
ARG VERSION
LABEL org.opencontainers.image.title="Krakarr" \
      org.opencontainers.image.description="Movies, series, subtitles, indexers and torrents in one container" \
      org.opencontainers.image.version="$VERSION"
ENV ASPNETCORE_HTTP_PORTS=8787 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0 \
    Krakarr__DownloadDirectory=/data/downloads \
    PUID=1000 \
    PGID=1000 \
    UMASK=002

# Subtitle sync tools (alass-cli, ffmpeg, ffprobe); Krakarr finds them on PATH.
COPY --from=tools /out/bin/ /usr/local/bin/
WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /entrypoint.sh
# /app (the binaries and web UI) must stay owned by root and read-only for PUID, so the app can't be made to rewrite its
# own code. COPY already gives root:root; the check fails the build if a later change loosens it. Krakarr writes only to
# /config, /data and /tmp, which is what lets the container run with read_only: true (see docker-compose.yml).
RUN chmod +x /entrypoint.sh && mkdir -p /config /data \
    && chmod -R go-w /app \
    && if [ -n "$(find /app /entrypoint.sh \( ! -user 0 -o ! -group 0 -o -perm /022 \) -print -quit)" ]; then \
         echo "/app must be owned by root and not writable by group/others" >&2; exit 1; \
       fi

# /data holds both downloads/ and the media library so imports can hardlink (one mount = one filesystem).
VOLUME ["/config", "/data"]
EXPOSE 8787 6881/tcp 6881/udp

# Built-in probe (no curl in the image): GET /api/v1/health on loopback, honouring the port and KRAKARR__URLBASE.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/Krakarr.Host.dll", "--healthcheck"]

ENTRYPOINT ["/entrypoint.sh"]
