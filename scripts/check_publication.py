"""Check all Git index contents before committing or publishing. Python 3 only.

Reports paths and categories, never matching credential values. This is a
preventive check, not proof that arbitrary secrets cannot be present.
"""
import pathlib
import re
import subprocess
import sys

PATTERNS = {
    "personal home path": rb"(?:[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s\"<>]+|/(?:home|Users)/[^/\s\"<>]+)",
    "private key": rb"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",
    "GitHub token": rb"(?:github_pat_[A-Za-z0-9_]{30,}|gh[pousr]_[A-Za-z0-9]{30,})",
    "AWS access key": rb"(?:AKIA|ASIA)[0-9A-Z]{16}",
    "service token": rb"(?:xox[baprs]-[A-Za-z0-9-]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{30,})",
    "credential in URL": rb"https?://[^/\s:@]+:[^/\s@]+@",
    "private network address": rb"\b(?:192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b",
    "Steam user directory": rb"userdata[\\/]+\d{5,}",
}
DENIED_SUFFIXES = {".dll", ".exe", ".pdb", ".timber", ".zip", ".7z", ".dmp", ".bundle", ".pem", ".key", ".pfx", ".p12", ".log"}
DENIED_PARTS = {"testlogs", "backups", "saves", "experimentalsaves", "bin", "obj", "__pycache__", "officialshaders", "assetbundles"}
IMAGES = {".png", ".jpg", ".jpeg"}


def inspect(path, data):
    p = pathlib.PurePosixPath(path)
    name = p.name.lower()
    if (p.suffix.lower() in DENIED_SUFFIXES or
            DENIED_PARTS.intersection(part.lower() for part in p.parts) or
            name in {"credentials.json", "localconfig.vdf", "loginusers.vdf", ".env"} or
            (name.startswith(".env.") and name != ".env.example") or
            name.endswith(".decompiled.cs")):
        return ["local/generated/credential file"]
    if p.suffix.lower() in IMAGES:
        return []  # Images require separate visual/metadata review.
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        data = data.decode("utf-16").encode("utf-8")
    if b"\0" in data:
        return ["unreviewed binary"]
    return [kind for kind, pattern in PATTERNS.items() if re.search(pattern, data, re.I)]


def main():
    entries = subprocess.check_output(["git", "ls-files", "--stage", "-z"]).split(b"\0")
    failures = 0
    count = 0
    for entry in entries:
        if not entry:
            continue
        metadata, raw_path = entry.split(b"\t", 1)
        mode, oid, stage = metadata.split()
        path = raw_path.decode("utf-8")
        count += 1
        if stage != b"0" or mode not in {b"100644", b"100755"}:
            problems = ["unmerged or unsupported Git entry"]
        else:
            data = subprocess.check_output(["git", "cat-file", "blob", oid.decode("ascii")])
            problems = inspect(path, data)
        for problem in problems:
            print(f"{path}: {problem}")
            failures += 1
    print(f"Checked {count} indexed files; {failures} findings.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
