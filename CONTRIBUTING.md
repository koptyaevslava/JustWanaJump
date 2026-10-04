# Contributing

Keep changes focused on JustWanaJump. Do not add unrelated mods, extracted game binaries, generated interop assemblies, local machine paths, or build output to the repository.

Use English for documentation, comments, identifiers, logs, commit messages, and issue or pull-request descriptions. Runtime localization strings may use the characters required by each supported language.

Before opening a pull request:

1. Build the plug-in against a local BepInEx installation.
2. Run `python scripts/check_release.py`.
3. Verify that no game DLL, generated interop assembly, executable, archive, or build output is staged.
4. Describe the manual in-game checks performed.
