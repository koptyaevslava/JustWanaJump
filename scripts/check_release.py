from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
CHECKED_SUFFIXES = {".cs", ".py", ".md", ".txt", ".json", ".yml", ".yaml", ".csproj"}
SKIPPED_PARTS = {".git", "bin", "obj", "dist", "payload"}
LOCAL_MACHINE = re.compile(r"[A-Za-z]:[\\/](?:Users|Program Files)[\\/]", re.IGNORECASE)
FORBIDDEN_SUFFIXES = {".dll", ".exe", ".jwjpkg", ".zip", ".7z", ".rar"}


def main() -> None:
    failures: list[str] = []
    for path in sorted(ROOT.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(ROOT)
        if any(part in SKIPPED_PARTS for part in relative.parts):
            continue
        if path.suffix.lower() in FORBIDDEN_SUFFIXES:
            failures.append(f"forbidden tracked artifact: {relative}")
        if path.suffix.lower() not in CHECKED_SUFFIXES:
            continue
        text = path.read_text(encoding="utf-8")
        for number, line in enumerate(text.splitlines(), 1):
            if LOCAL_MACHINE.search(line):
                failures.append(f"local machine path: {relative}:{number}")
    if failures:
        raise SystemExit("Release checks failed:\n" + "\n".join(failures))
    print("Source and release structure checks passed.")


if __name__ == "__main__":
    main()
