#!/usr/bin/env python3
"""Pre-push copyright, binary, and privacy auditor for public repositories.

Fails non-zero if any forbidden game files, large binaries, personal paths/names,
emails, or missing SwitchRecomp/ScarletNX license/NOTICE files are detected.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

FORBIDDEN_EXTS = {
    ".nup", ".nsp", ".xci", ".nsz", ".xcz", ".nca", ".romfs",
    ".ard", ".append", ".arh", ".sav", ".nacp", ".npdm", ".snxs",
    ".nso", ".nro", ".pck", ".nop", ".wilay", ".wimdo", ".wismt",
    ".wismda", ".wishp", ".bdat", ".exe", ".dll", ".pdb", ".ppm",
    ".zip", ".7z", ".rar", ".tar", ".gz",
}

FORBIDDEN_FILENAMES = {
    "mail.txt", "retomada.md", "handoff.md", "nvngx_dlss.dll",
    "bf2.ard", "bf2.arh", "control.nacp", "bf2savefile.sav",
}

TEXT_EXTS = {
    ".cs", ".csproj", ".sln", ".c", ".h", ".cpp", ".hpp",
    ".js", ".ts", ".html", ".css", ".json", ".md", ".txt",
    ".ps1", ".sh", ".py", ".tsv", ".yml", ".yaml", ".xml", ".manifest",
    ".gitignore", ".apache", "",
}

SENSITIVE_PATTERNS = [
    (re.compile(r"(?i)c:\\users\\(?!x\\)"), "Hardcoded Windows user profile path"),
    (re.compile(r"(?i)c:/users/(?!x/)"), "Hardcoded Windows user profile path"),
    (re.compile(r"(?i)f:\\xenoblade 2 pc edition"), "Hardcoded local dev workspace path"),
    (re.compile(r"(?i)f:/xenoblade 2 pc edition"), "Hardcoded local dev workspace path"),
    (re.compile(r"(?i)c:\\snx_build|c:/snx_build"), "Hardcoded local C:\\snx_build path"),
    (re.compile(r"(?i)e:\\ps5|e:/ps5"), "Hardcoded personal drive E:\\PS5 path"),
    (re.compile(r"(?i)legacy_vault_2026|sw2_pc_20261118"), "Hardcoded internal dev vault path"),
    (re.compile(r"(?i)danila|igusmarine|marinhoigordaniel"), "Personal username/email leak"),
]

REQUIRED_FILES = ["LICENSE", "LICENSE.apache", "NOTICE", "README.md", ".gitignore"]
MAX_FILE_BYTES = 2 * 1024 * 1024  # 2 MiB


def iter_repo_files(root: Path):
    git_dir = root / ".git"
    if git_dir.exists():
        out = subprocess.check_output(
            ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        )
        for rel in out.split(b"\0"):
            if rel:
                p = root / rel.decode("utf-8")
                if p.is_file():
                    yield p
        return

    skip_dirs = {".git", "bin", "obj", "out", "node_modules", "__pycache__", "build", "release"}
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        if any(part.lower() in skip_dirs for part in p.relative_to(root).parts):
            continue
        yield p


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=str(Path(__file__).resolve().parent.parent))
    args = ap.parse_args()
    root = Path(args.root).resolve()

    errors: list[str] = []

    for req in REQUIRED_FILES:
        if not (root / req).is_file():
            errors.append(f"Missing required file: {req}")

    notice = root / "NOTICE"
    if notice.is_file():
        txt = notice.read_text(encoding="utf-8", errors="ignore")
        if "SwitchRecomp" not in txt or "MZ Labs" not in txt:
            errors.append("NOTICE does not include required credit to 'SwitchRecomp by MZ Labs'")

    checked = 0
    for fp in sorted(iter_repo_files(root)):
        checked += 1
        rel = fp.relative_to(root).as_posix()
        lname = fp.name.lower()
        ext = fp.suffix.lower()

        if lname in FORBIDDEN_FILENAMES:
            errors.append(f"Forbidden file: {rel}")
        if ext in FORBIDDEN_EXTS:
            errors.append(f"Forbidden extension ({ext}): {rel}")
        sz = fp.stat().st_size
        if sz > MAX_FILE_BYTES:
            errors.append(f"File exceeds 2 MiB limit ({sz / (1024 * 1024):.2f} MiB): {rel}")

        # Skip auditing this script's own regex definitions
        if rel == "tools/audit_public_repo.py":
            continue

        if ext in TEXT_EXTS or fp.name in REQUIRED_FILES:
            try:
                content = fp.read_text(encoding="utf-8", errors="ignore")
            except OSError as exc:
                errors.append(f"Cannot read {rel}: {exc}")
                continue
            for lineno, line in enumerate(content.splitlines(), 1):
                for pat, desc in SENSITIVE_PATTERNS:
                    if pat.search(line):
                        # Allow explicit negative test assertion in launcher/Tests/Program.cs
                        if rel == "launcher/Tests/Program.cs" and "Must never fall back" in line:
                            continue
                        errors.append(f"{desc} at {rel}:{lineno}: {line.strip()[:120]}")

    if (root / ".git").exists():
        log_out = subprocess.run(
            ["git", "-C", str(root), "log", "--format=%h %an <%ae> | %cn <%ce>"],
            capture_output=True, text=True, check=False,
        )
        if log_out.returncode == 0:
            for line in log_out.stdout.splitlines():
                if re.search(r"(?i)danila|igusmarine|marinhoigordaniel", line):
                    errors.append(f"Personal git commit identity found: {line}")

    if errors:
        print(f"AUDIT FAILED ({len(errors)} issue(s) across {checked} files):")
        for e in errors:
            print("  [FAIL]", e)
        return 1

    print(f"AUDIT PASSED: {checked} files verified clean (0 leaks, 0 copyrighted assets, NOTICE OK).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
