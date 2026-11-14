# UDP playback regression fixes

The Studio isolated lab now supports known WiZ, LIFX and Govee bulbs alongside
REST; see Studio `docs/testing/local-lab.md` and `udp-lab-verification.md`.
Fixtures use separate internal Docker IPs and standard-port byte-preserving relays,
not host networking or physical bulbs. Discovery/Identify remain separate coverage.

Integrated validation exposed real driver bugs rather than relaxing the simulator:

- LIFX packets now use the standard 36-byte LAN header, type at byte 32 and payload
  at byte 36. LightState parsing respects power and separates chroma/intensity, so
  restoring a dim light does not apply brightness twice.
- LIFX and Govee `SetState` turn an off/unknown bulb on for nonzero brightness;
  zero-brightness initialization does not falsely cache it as on. Fade-to-black
  retains an already-powered bulb until the explicit end power-off.
- Govee frame dispatch sends turn without waiting for ACKs, plus color and initial
  brightness even if the first requested brightness is 100%. Explicit `SetPower`
  retains its existing retry behavior. Configured ports follow WiZ/LIFX convention.
- WiZ zero-brightness state uses power off rather than turning on at minimum dimming.

`dotnet test -c Release` on .NET 8: 122 passed, none skipped. `LifxWireTests` capture
real UDP packets and return an independently constructed standard LightState;
`UdpPowerTests` check WiZ off/on intent and Govee's complete nonblocking initial
state. Studio's mixed 5.0 case passed actual Emby Web and controlled-session
playback with independent WiZ neighbors, dim LIFX, Govee and REST rendering,
per-protocol receipts and Stop/off restoration. A deliberately wrong Govee receipt
failed. Source identities and restricted evidence live in the Studio lab artifacts.

This is focused playback evidence, not a completed J1 acceptance matrix or hardware
certification. Studio's existing traceability failures remain documented; no gates
were weakened. These plugin changes must accompany the Studio/simulator UDP lab
updates.
