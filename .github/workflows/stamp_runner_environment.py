#!/usr/bin/env python3
"""Stamp the measured runner environment into a benchmark artifact.

Adds ONLY ``Metadata.RunnerImage`` and ``Metadata.Sdk`` to the artifact at
``--artifact``. Measured numbers are never touched. The image version (the
part that drifts under a stable runner label) is read from the live runner:
the ``ImageVersion`` environment variable first, then the ``imageversion``
key of ``~/imagedata.json``. The image family (e.g. ``macos-26-arm64``) is a
static property of the job's pinned runner and is passed explicitly with
``--image-family``; it is a declared fact, not a measurement. The stamped
value is ``{family}/{version}`` verbatim - no normalization, no defaults.

Fail-closed rules: an existing differing value is never overwritten (exit 2,
file untouched); when the image cannot be determined, ``RunnerImage`` stays
absent and a warning goes to stderr (the comparator then reports
UNCALIBRATED instead of adjudicating); a malformed artifact fails loudly.
The SDK is informational only and is never compared by the gate.
"""

import argparse
import json
import os
import subprocess
import sys


def read_image_version(environ, imagedata_path):
    """The image version from the live runner, or None when undeterminable."""
    version = (environ.get("ImageVersion") or "").strip()
    if version:
        return version
    try:
        with open(imagedata_path, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return None
    if isinstance(data, dict):
        for key in ("imageversion", "ImageVersion", "IMAGEVERSION"):
            value = data.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return None


def read_sdk_version(explicit):
    """The SDK version: explicit --sdk wins, else `dotnet --list-sdks` max."""
    if explicit and explicit.strip():
        return explicit.strip()
    try:
        proc = subprocess.run(
            ["dotnet", "--list-sdks"], capture_output=True, text=True, timeout=60
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if proc.returncode != 0:
        return None
    versions = []
    for line in proc.stdout.splitlines():
        token = line.split()[0] if line.split() else ""
        parts = token.split(".")
        if len(parts) >= 3 and all(p.isdigit() for p in parts[:3]):
            versions.append(token)
    if not versions:
        return None
    return sorted(versions, key=lambda v: [int(p) for p in v.split(".")[:3]])[-1]


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
    version = read_image_version(os.environ, imagedata_path)
    if version is None:
        print(
            "stamp_runner_environment: warning: image version "
            "undeterminable (no ImageVersion env, no usable imagedata.json); "
            "RunnerImage left absent",
            file=sys.stderr,
        )
    else:
        stamped = f"{args.image_family}/{version}"
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
