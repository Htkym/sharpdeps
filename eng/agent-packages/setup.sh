#!/usr/bin/env sh
# This wrapper does not download, install, register or alter a host profile.
set -eu
operation=${1:-export}
if [ "$#" -gt 0 ]; then shift; fi
case "$operation" in
  export|remove) exec sharpdeps "agent-$operation" "$@" ;;
  *) printf '%s\n' 'Usage: setup.sh export|remove [SharpDeps arguments]' >&2; exit 2 ;;
esac
