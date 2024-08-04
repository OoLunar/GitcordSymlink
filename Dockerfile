FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
ARG VERSION=1.0.0
WORKDIR /src

COPY ./ /src
RUN apk add git --no-cache \
    && git submodule update --init --recursive \
    && sed -i "s/<Version>.*<\/Version>/<Version>${VERSION}<\/Version>/" src/GitcordSymlink.csproj \
    && dotnet publish -c Release -r linux-musl-x64

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /src

COPY --from=build /src/src/bin/Release/net8.0/linux-musl-x64/publish /app
RUN apk upgrade --no-cache --available && apk add openssl icu-libs --no-cache
ENTRYPOINT ["/app/GitcordSymlink"]