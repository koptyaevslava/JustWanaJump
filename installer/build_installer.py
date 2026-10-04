from __future__ import annotations

import hashlib
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import zipfile


VERSION = "0.2.1"
ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parent
SOURCE = WORKSPACE / "payload/CosmeticJump"
OUTPUT = WORKSPACE / f"dist/JustWanaJump-Installer-{VERSION}"
PACKAGE = OUTPUT / f"JustWanaJump-{VERSION}.jwjpkg"
EXE = OUTPUT / "JustWanaJumpInstaller.exe"
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def active_payload_files() -> list[Path]:
    required = [SOURCE / "CosmeticJump.dll", SOURCE / "README.txt"]
    missing = [path for path in required if not path.is_file()]
    if missing:
        raise RuntimeError(f"Required payload files are missing: {missing}")
    actual = sorted(path for path in SOURCE.rglob("*") if path.is_file())
    if actual != sorted(required):
        raise RuntimeError(f"Payload must contain only CosmeticJump.dll and README.txt: {actual}")
    return required


def build_package() -> tuple[int, int]:
    selected = active_payload_files()
    records: list[tuple[str, int, str]] = []
    total = 0
    for path in selected:
        relative = str(PurePosixPath(path.relative_to(SOURCE).as_posix()))
        size = path.stat().st_size
        records.append((relative, size, sha256(path)))
        total += size

    manifest = "# path\tbytes\tsha256\n" + "".join(
        f"{path}\t{size}\t{digest}\n" for path, size, digest in records
    )
    info = (
        "Format=1\n"
        "ModId=local.seaofstars.cosmeticjump\n"
        "Name=JustWanaJump\n"
        f"Version={VERSION}\n"
        f"Files={len(records)}\n"
        f"Bytes={total}\n"
    )
    OUTPUT.mkdir(parents=True, exist_ok=True)
    temporary = PACKAGE.with_suffix(".jwjpkg.tmp")
    temporary.unlink(missing_ok=True)
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("installer-info.ini", info.encode("utf-8"))
        archive.writestr("installer-manifest.tsv", manifest.encode("utf-8"))
        for relative, _, _ in records:
            archive.write(SOURCE / Path(relative), "payload/" + relative)
    temporary.replace(PACKAGE)
    return len(records), total


def build_exe() -> None:
    if not CSC.is_file():
        raise RuntimeError(f".NET Framework compiler not found: {CSC}")
    command = [
        str(CSC), "/nologo", "/target:winexe", "/optimize+", "/platform:anycpu",
        f"/out:{EXE}", f"/win32manifest:{ROOT / 'app.manifest'}",
        "/reference:System.dll", "/reference:System.Core.dll", "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll", "/reference:System.IO.Compression.dll",
        "/reference:System.IO.Compression.FileSystem.dll", str(ROOT / "Installer.cs"),
    ]
    subprocess.run(command, check=True, cwd=WORKSPACE)


def write_readme(count: int, total: int) -> None:
    text = f"""Sea of Stars JustWanaJump {VERSION}

1. Keep JustWanaJumpInstaller.exe and JustWanaJump-{VERSION}.jwjpkg in the same folder.
2. Run JustWanaJumpInstaller.exe.
3. The installer detects a Steam installation or lets you select the folder that contains SeaOfStars.exe.
4. Select INSTALL.

The package contains only JustWanaJump files: the plug-in DLL and README.
It does not include BepInEx, game files, or any other mod. Install BepInEx 6 for IL2CPP first.

Installation uses a staging directory, verifies every SHA-256 checksum, and rolls back if the final swap fails.
REMOVE MOD deletes only BepInEx/plugins/CosmeticJump. The BepInEx configuration file is preserved.

Do not upload this installer bundle to Nexus Mods. Use the separate Nexus manual-install ZIP.

Package files: {count}
Unpacked size: {total} bytes
"""
    (OUTPUT / "README.txt").write_text(text, encoding="utf-8")
    (OUTPUT / "DO-NOT-UPLOAD-TO-NEXUS.txt").write_text(
        "This bundle contains an executable and an archive container. Use the dedicated Nexus ZIP instead.\n",
        encoding="utf-8",
    )


def clean_output() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for path in OUTPUT.iterdir():
        if path.is_file():
            path.unlink()
        elif path.is_dir():
            shutil.rmtree(path)


def main() -> None:
    clean_output()
    count, total = build_package()
    build_exe()
    write_readme(count, total)
    package_hash = sha256(PACKAGE)
    exe_hash = sha256(EXE)
    (OUTPUT / f"{PACKAGE.name}.sha256").write_text(
        f"{package_hash}  {PACKAGE.name}\n", encoding="ascii"
    )
    (OUTPUT / f"{EXE.name}.sha256").write_text(
        f"{exe_hash}  {EXE.name}\n", encoding="ascii"
    )
    print({
        "files": count,
        "bytes": total,
        "packageBytes": PACKAGE.stat().st_size,
        "packageSha256": package_hash,
        "exeBytes": EXE.stat().st_size,
        "exeSha256": exe_hash,
    })


if __name__ == "__main__":
    main()
