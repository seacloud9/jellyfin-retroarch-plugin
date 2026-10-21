# Headless Linux RetroArch + Nestopia for scripts/jellyfin-smoke.py --native-linux-docker.
# Teardown: see docs/linux-native-docker.md.
# BASE is the .NET runtime for the dev build, or plain Ubuntu (no .NET) for the self-contained companion package.
ARG BASE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:0cf09aaccf6a9f68900aed4c73fb28fe25bb59ec85de4dea08ba02240a8635f3
FROM ${BASE}
LABEL org.retrobridge.smoke="true"
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends retroarch libretro-nestopia python3 \
 && rm -rf /var/lib/apt/lists/* \
 && install -D -m 0644 "$(find /usr/lib -name nestopia_libretro.so | head -n 1)" /opt/cores/nestopia_libretro.so \
 && useradd --create-home retro
USER retro
# Null drivers: no display, audio, or input device exists in the container.
RUN mkdir -p /home/retro/.config/retroarch && printf '%s\n' \
    'video_driver = "null"' 'audio_driver = "null"' 'input_driver = "null"' \
    'joypad_driver = "null"' 'menu_driver = "null"' \
    > /home/retro/.config/retroarch/retroarch.cfg
WORKDIR /home/retro
