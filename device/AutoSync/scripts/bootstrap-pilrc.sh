#!/bin/sh
set -eu

output_path=${1:-build/tools/pilrc}
archive_sha256=f3d6ea3c77f5d2a00707f4372a212377ab7bd77b3d68c3db7e28a553b235903f
archive_url='https://master.dl.sourceforge.net/project/pilrc/pilrc/3.2/pilrc-3.2.tar.gz?viasf=1'
script_dir=$(CDPATH= cd "$(dirname "$0")" && pwd)
patch_file="$script_dir/pilrc-3.2-lp64.patch"
patch_sha256=$(sha256sum "$patch_file" | cut -d ' ' -f 1)
tool_dir=$(dirname "$output_path")
archive_path="$tool_dir/pilrc-3.2.tar.gz"
archive_part="$archive_path.part"
source_dir="$tool_dir/pilrc-3.2"
patch_stamp="$tool_dir/pilrc-3.2-lp64.stamp"

if test -x "$output_path" && test -f "$patch_stamp" &&
    test "$(cat "$patch_stamp")" = "$patch_sha256"; then
    exit 0
fi

mkdir -p "$tool_dir"
if test -f "$archive_path" &&
    ! printf '%s  %s\n' "$archive_sha256" "$archive_path" |
        sha256sum -c - >/dev/null 2>&1; then
    rm -f "$archive_path"
fi
if ! test -f "$archive_path"; then
    rm -f "$archive_part"
    trap 'rm -f "$archive_part"' 0 1 2 15
    curl -L --fail --retry 2 -o "$archive_part" "$archive_url"
    printf '%s  %s\n' "$archive_sha256" "$archive_part" | sha256sum -c -
    mv "$archive_part" "$archive_path"
    trap - 0 1 2 15
fi

printf '%s  %s\n' "$archive_sha256" "$archive_path" | sha256sum -c -

if ! test -d "$source_dir"; then
    tar -xzf "$archive_path" -C "$tool_dir"
fi

if patch --dry-run -p1 -d "$source_dir" < "$patch_file" >/dev/null 2>&1; then
    patch -p1 -d "$source_dir" < "$patch_file"
elif patch --dry-run -R -p1 -d "$source_dir" < "$patch_file" \
        >/dev/null 2>&1; then
    : # The patch is already present in this source tree.
else
    printf '%s\n' 'Unable to apply the PilRC 3.2 LP64 bitmap patch.' >&2
    exit 1
fi

(
    cd "$source_dir/unix"
    ./configure --disable-pilrcui
    make
)
cp "$source_dir/unix/pilrc" "$output_path"
printf '%s\n' "$patch_sha256" > "$patch_stamp"
