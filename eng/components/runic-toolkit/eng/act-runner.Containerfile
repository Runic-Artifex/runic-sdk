ARG RUNNER_IMAGE
FROM ${RUNNER_IMAGE}

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
      libasound2t64 \
      libatk-bridge2.0-0t64 \
      libatk1.0-0t64 \
      libcairo2 \
      libcups2t64 \
      libdbus-1-3 \
      libexpat1 \
      libgbm1 \
      libglib2.0-0t64 \
      libnss3 \
      libpango-1.0-0 \
      libxcomposite1 \
      libxdamage1 \
      libxfixes3 \
      libxkbcommon0 \
      libxrandr2 \
      powershell \
    && rm -rf /var/lib/apt/lists/*
