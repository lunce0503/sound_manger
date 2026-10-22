# Project conventions

- Release Windows builds as a single `.exe` asset. Do not publish a ZIP as the Windows application download.
- Keep Android release signing keys unchanged across updates. Never commit private keys or authentication tokens.
- Keep server addresses and connection keys out of the main listening UI; show them only inside connection/authentication settings.
- Keep 360-degree direction interaction out of the main scrolling device list. Device mute controls use a speaker icon, the label "음소거", and a toggle.
- Preserve saved pairing on app close and transient network failures. Reuse it for automatic reconnection.
