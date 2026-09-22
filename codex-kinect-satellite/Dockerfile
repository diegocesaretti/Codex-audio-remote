FROM python:3.11-slim-bookworm

ARG BUILD_VERSION=0.1.0
ARG BUILD_ARCH=aarch64
LABEL io.hass.version="$BUILD_VERSION" \
      io.hass.type="app" \
      io.hass.arch="$BUILD_ARCH"

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1

# kinect-audio-setup lives in Debian contrib. Extract the package without
# running its postinst so Microsoft's non-redistributable UAC firmware is NOT
# baked into the image; run.sh fetches it locally on the user's Pi.
RUN sed -i 's/Components: main/Components: main contrib non-free-firmware/' /etc/apt/sources.list.d/debian.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates wget unzip p7zip-full libusb-1.0-0 libasound2 libasound2-plugins libatomic1 libgomp1 pulseaudio-utils usbutils \
 && cd /tmp \
 && apt-get download kinect-audio-setup \
 && mkdir /tmp/kinectpkg \
 && dpkg-deb -x /tmp/kinect-audio-setup_*.deb /tmp/kinectpkg \
 && cp /tmp/kinectpkg/usr/sbin/kinect_fetch_fw /usr/local/sbin/ \
 && cp /tmp/kinectpkg/usr/sbin/kinect_upload_fw /usr/local/sbin/ \
 && rm -rf /var/lib/apt/lists/* /tmp/kinect* \
 && pip install --no-cache-dir numpy==2.1.3 vosk==0.3.45 websockets==15.0.1 aiohttp==3.12.15

WORKDIR /app
COPY satellite.py /app/satellite.py
COPY diagnostic_ui.py /app/diagnostic_ui.py
COPY run.sh /app/run.sh
RUN chmod +x /app/run.sh /app/satellite.py

CMD ["/app/run.sh"]
