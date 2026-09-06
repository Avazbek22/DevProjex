# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG DEVPROJEX_VERSION=5.2
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in \
      amd64) rid="linux-x64" ;; \
      arm64) rid="linux-arm64" ;; \
      *) echo "Unsupported container architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    dotnet publish Apps/TerminalHost/DevProjex.TerminalHost.csproj \
      -c Release \
      -r "$rid" \
      --self-contained true \
      -p:DevProjexVersion="$DEVPROJEX_VERSION" \
      -p:PublishSingleFile=false \
      -p:PublishTrimmed=false \
      -p:PublishReadyToRun=false \
      -p:DevProjexGrammarDelivery=Content \
      -p:DevProjexGenerateReleasePayloadReceipt=true \
      -p:DevProjexGenerateFolderPayloadReceipt=true \
      -p:DevProjexPayloadReceiptDirectory=/out/receipt \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -o /out/app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out/app/ ./
COPY --from=build /out/receipt/ /payload-receipt/
ENV PATH="/app:${PATH}"
USER app
ENTRYPOINT ["devprojex"]
