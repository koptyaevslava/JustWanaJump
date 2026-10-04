# Nexus Mods packaging

Upload only the dedicated manual-install ZIP to Nexus Mods. It contains `BepInEx/plugins/CosmeticJump/CosmeticJump.dll` and a plain-text README.

The Nexus package must meet all of these checks:

- Standard ZIP format
- No executable files
- No nested archives
- No password protection
- No game files, BepInEx binaries, generated interop assemblies, or unrelated mods
- SHA-256 digest published next to the download

If Nexus Mods quarantines an upload, keep the file available and contact Nexus Mods support with the mod-page link and the published SHA-256 digest.
