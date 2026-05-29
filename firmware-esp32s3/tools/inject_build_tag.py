"""Inject a UTC build-time tag (and the repo slug) as -D flags so the
firmware knows which build it is running and where to look for OTA updates.

Outputs two flags:
  -DTINYCHAOS_BUILD_TAG=<tag>     used in the boot banner and OTA "running vs latest" comparison.
  -DTINYCHAOS_REPO_SLUG=<slug>    used by OtaUpdater to hit api.github.com.

In CI, GITHUB_REF_NAME (the tag name on tag pushes) and GITHUB_REPOSITORY
take precedence so the firmware released by GitHub Actions reports the
release tag rather than a UTC timestamp.

Locally (no env vars set), we fall back to a UTC timestamp and the
hardcoded gotnull/tinychaos slug.

Adapted from rsvpnano's inject_build_tag.py.
"""
import datetime
import os

Import("env")  # noqa: F821 (provided by SCons)

tag = (
    os.environ.get("TINYCHAOS_BUILD_TAG")
    or os.environ.get("GITHUB_REF_NAME")
    or datetime.datetime.utcnow().strftime("%y%m%d-%H%M%S")
)
repo = os.environ.get("GITHUB_REPOSITORY") or "gotnull/tinychaos"

env.Append(BUILD_FLAGS=[
    f'-DTINYCHAOS_BUILD_TAG=\\"{tag}\\"',
    f'-DTINYCHAOS_REPO_SLUG=\\"{repo}\\"',
])
print(f"-- tinychaos build tag: {tag}")
print(f"-- tinychaos repo slug: {repo}")
