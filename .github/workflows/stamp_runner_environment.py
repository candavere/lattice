#!/usr/bin/env python3
"""Stamp the measured runner environment into a benchmark artifact.

Adds ONLY ``Metadata.RunnerImage`` and ``Metadata.Sdk`` to the artifact at
``--artifact``. Measured numbers are never touched. The image version (the
part that drifts under a stable runner label) is read from the live runner,
in this order: (1) the ``Image Release:`` URL in ``~/imagedata.json`` (its
tag is the canonical release identity, exactly the form the reference record
transcribes, e.g. ``macos-26-arm64/20260907.0351``); (2) the ``ImageVersion``
environment variable combined with the static ``--image-family`` (verbatim -
this can carry a provisioner revision suffix such as ``.1`` and then honestly
will not equal the release tag); (3) absent with a stderr warning. The image
family is a static property of the job's pinned runner, a declared fact, not
a measurement. No normalization, no defaults, no guessing.

Fail-closed rules: an existing differing value is never overwritten (exit 2,
file untouched); when the image cannot be determined, ``RunnerImage`` stays
absent and a warning goes to stderr (the comparator then reports
UNCALIBRATED instead of adjudicating); a malformed artifact fails loudly.
The SDK is the selected SDK (`dotnet --version` from the repo
working directory, honoring global.json), informational only and never
compared by the gate. If it cannot be determined, Sdk stays absent.
"""

import argparse
import json
import os
import re
import subprocess
import sys


def read_release_tag(text):
    """The canonical ``family/version`` image identity from an
    ``Image Release: .../releases/tag/{family}%2F{version}`` URL, or None."""
    import re
    import urllib.parse

    match = re.search(r"releases/tag/(\S+)", text)
    if not match:
        return None
    tag = urllib.parse.unquote(match.group(1)).strip()
    if "/" not in tag:
        return None
    return tag


def read_image_identity(environ, imagedata_path, image_family):
    """The exact image identity from the live runner, or None.

    Prefers the release tag embedded in imagedata.json (the canonical
    release identity); falls back to ImageVersion + the static family.
    """
    try:
        with open(imagedata_path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        data = None
    if isinstance(data, list):
        for group in data:
            if isinstance(group, dict):
                tag = read_release_tag(str(group.get("detail") or ""))
                if tag:
                    return tag, "imagedata-release-tag"
    version = (environ.get("ImageVersion") or "").strip()
    if version:
        return f"{image_family}/{version}", "ImageVersion-env"
    return None, None


def looks_like_version(text) -> bool:
    """Trimmed `dotnet --version` output counts only if version-shaped."""
    return re.match(r"\d+\.\d+\.\d+", text.strip() or "") is not None


def read_sdk_version(explicit):
    """The selected SDK: explicit --sdk wins, else `dotnet --version`.

    Never the max installed SDK: with a floating `dotnet-version` the
    selected SDK can differ from the highest one on disk, so writing the
    max would be a wrong claim. Unparseable output leaves Sdk absent.
    """
    if explicit and explicit.strip():
        return explicit.strip()
    try:
        proc = subprocess.run(
            ["dotnet", "--version"], capture_output=True, text=True, timeout=60
        )
    except (OSError, subprocess.SubprocessError):
        print(
            "stamp_runner_environment: warning: `dotnet --version` failed; "
            "Sdk left absent",
            file=sys.stderr,
        )
        return None
    if proc.returncode != 0:
        print(
            "stamp_runner_environment: warning: `dotnet --version` failed; "
            "Sdk left absent",
            file=sys.stderr,
        )
        return None
    version = (proc.stdout or "").strip().splitlines()
    version = version[0].strip() if version else ""
    if not looks_like_version(version):
        print(
            "stamp_runner_environment: warning: `dotnet --version` output "
            "unparseable; Sdk left absent",
            file=sys.stderr,
        )
        return None
    return version




def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--artifact", required=True, help="benchmark artifact JSON to stamp in place"
    )
    parser.add_argument(
        "--image-family",
        required=True,
        help="static image family of the pinned runner (e.g. macos-26-arm64)",
    )
    parser.add_argument(
        "--imagedata-path", default=None, help="override for ~/imagedata.json (tests)"
    )
    parser.add_argument(
        "--sdk", default=None, help="explicit SDK version (tests); else autodetected"
    )
    args = parser.parse_args(argv)

    try:
        with open(args.artifact, encoding="utf-8") as handle:
            artifact = json.load(handle)
    except (OSError, ValueError) as exc:
        print(f"stamp_runner_environment: cannot read artifact: {exc}", file=sys.stderr)
        return 2
    if not isinstance(artifact, dict) or not isinstance(artifact.get("Metadata"), dict):
        print(
            "stamp_runner_environment: artifact has no Metadata object", file=sys.stderr
        )
        return 2
    metadata = artifact["Metadata"]

    imagedata_path = args.imagedata_path or os.path.join(
        os.path.expanduser("~"), "imagedata.json"
    )
    stamped, _source = read_image_identity(
        os.environ, imagedata_path, args.image_family
    )
    if stamped is None:
        print(
            "stamp_runner_environment: warning: image identity "
            "undeterminable (no release tag in imagedata.json, no "
            "ImageVersion env); RunnerImage left absent",
            file=sys.stderr,
        )
    else:
        existing = metadata.get("RunnerImage")
        if (
            isinstance(existing, str)
            and existing.strip()
            and existing.strip() != stamped
        ):
            print(
                f"stamp_runner_environment: refusing to overwrite "
                f"RunnerImage {existing!r} with {stamped!r}",
                file=sys.stderr,
            )
            return 2
        metadata["RunnerImage"] = stamped

    sdk = read_sdk_version(args.sdk)
    if sdk is not None:
        existing_sdk = metadata.get("Sdk")
        if (
            isinstance(existing_sdk, str)
            and existing_sdk.strip()
            and existing_sdk.strip() != sdk
        ):
            print(
                f"stamp_runner_environment: refusing to overwrite "
                f"Sdk {existing_sdk!r} with {sdk!r}",
                file=sys.stderr,
            )
            return 2
        metadata["Sdk"] = sdk

    try:
        with open(args.artifact, "w", encoding="utf-8") as handle:
            json.dump(artifact, handle, indent=2)
            handle.write("\n")
    except OSError as exc:
        print(
            f"stamp_runner_environment: cannot write artifact: {exc}", file=sys.stderr
        )
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
