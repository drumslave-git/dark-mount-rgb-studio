#!/usr/bin/env bash
# Prints the version of the next release, without the leading "v".
#
#   next-version.sh [auto|patch|minor|major]
#
# The last release is the highest vX.Y.Z tag. With "auto" (the default) the commit messages
# since that tag decide what to raise:
#   a breaking change ("feat!: ...", "fix(page)!: ...", or a "BREAKING CHANGE:" line)  -> major
#   a new feature ("feat: ...", "feat(tray): ...")                                    -> minor
#   anything else                                                                     -> patch
# Without any release tag yet the answer is 1.0.0.
set -euo pipefail

bump="${1:-auto}"
case "$bump" in
  auto | patch | minor | major) ;;
  *) echo "usage: next-version.sh [auto|patch|minor|major]" >&2; exit 2 ;;
esac

last="$(git tag --list 'v*' --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' || true)"
last="${last%%$'\n'*}"
if [ -z "$last" ]; then
  echo "1.0.0"
  exit 0
fi

if [ "$bump" = auto ]; then
  log="$(git log "$last..HEAD" --format='%s%n%b')"
  if grep -qE '^[a-z]+(\([^)]*\))?!:|^BREAKING[ -]CHANGE:' <<<"$log"; then
    bump=major
  elif grep -qE '^feat(\([^)]*\))?:' <<<"$log"; then
    bump=minor
  else
    bump=patch
  fi
fi

IFS=. read -r major minor patch <<<"${last#v}"
case "$bump" in
  major) echo "$((major + 1)).0.0" ;;
  minor) echo "$major.$((minor + 1)).0" ;;
  patch) echo "$major.$minor.$((patch + 1))" ;;
esac
