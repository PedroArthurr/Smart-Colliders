#!/bin/sh
# snapshot tool code into refs/snapshots/<name> without touching branch/index
cd "$(dirname "$0")/.."
export GIT_INDEX_FILE=.git/snapshot-index
rm -f $GIT_INDEX_FILE
git read-tree HEAD
git add Packages/com.reclabs.smart.colliders CLAUDE.md
T=$(git write-tree)
C=$(git commit-tree $T -p HEAD -m "snapshot $1")
git update-ref refs/snapshots/$1 $C
rm -f $GIT_INDEX_FILE
echo "$1 -> $C"
