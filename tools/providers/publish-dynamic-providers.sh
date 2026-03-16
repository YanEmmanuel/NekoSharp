#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 5 || $# -gt 6 ]]; then
    echo "Usage: $0 <source-dll> <target-dll> <manifest-path> <assembly-url> <version> [package-name]" >&2
    exit 1
fi

source_dll="$1"
target_dll="$2"
manifest_path="$3"
assembly_url="$4"
version="$5"
package_name="${6:-NekoSharp.DynamicProviders}"

mkdir -p "$(dirname "$target_dll")"
mkdir -p "$(dirname "$manifest_path")"

cp "$source_dll" "$target_dll"

if command -v sha256sum >/dev/null 2>&1; then
    sha256="$(sha256sum "$target_dll" | awk '{print toupper($1)}')"
else
    sha256="$(shasum -a 256 "$target_dll" | awk '{print toupper($1)}')"
fi

cat > "$manifest_path" <<EOF
{
  "providers": [
    {
      "name": "$package_name",
      "version": "$version",
      "assemblyUrl": "$assembly_url",
      "sha256": "$sha256",
      "enabled": true
    }
  ]
}
EOF
