# Current host transport fixture

This additive package-consumer fixture combines exact local Runic.Application,
Runic.Application.Bridge, and Runic.Application.Hosting candidates with an
exact local @runic-artifex/application-bridge archive. The committed
generated/ directory is produced from application.bridge.ts and checked before every
journey; fixtures.mjs likewise checks every committed conformance frame.
HostConsumer supplies its manifest and every schema to the packaged
Runic.Application.Bridge analyzer as AdditionalFiles. It exercises the public
FrameChannel/WebSocket contract for initialization, command/receipt and event
order, reconnect, untrusted-origin rejection, malformed/oversize frames, and
controlled teardown.

It is deliberately local and headless. Authentication, remote service
transport, deployment, SSR, hydration, and rollout are not covered here.

Run it with isolated candidate inputs:

    RUNIC_CURRENT_HOST_TRANSPORT_NUGET_FEED=/path/to/feed \
    RUNIC_CURRENT_HOST_TRANSPORT_APPLICATION_VERSION=0.2.0-preview.example \
    RUNIC_CURRENT_HOST_TRANSPORT_NPM_ARCHIVE=/path/to/application-bridge.tgz \
    node eng/current-host-transport/verify.mjs run-twice ../.github/runic.release.json
