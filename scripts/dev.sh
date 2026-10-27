#!/usr/bin/env sh
set -eu

prepare_only=false
skip_build=false
if [ "${1:-}" = "--prepare" ]; then prepare_only=true; shift; fi
if [ "${1:-}" = "--no-build" ]; then skip_build=true; shift; fi

cd "$(dirname "$0")/.."

sdk_version=$(sed -n 's/.*PackageVersion Include="ShiroBot.SDK" Version="\([^"]*\)".*/\1/p' Directory.Packages.props)
if [ -z "$sdk_version" ]; then
  echo 'Cannot read ShiroBot.SDK version from Directory.Packages.props.' >&2
  exit 1
fi

case "$(uname -s)" in
  Darwin) platform=osx ;;
  Linux)
    platform=linux
    if [ -f /etc/alpine-release ]; then platform=linux-musl; fi
    ;;
  *) echo 'Unsupported operating system. On Windows, use dev.ps1.' >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) architecture=x64 ;;
  arm64|aarch64) architecture=arm64 ;;
  *) echo 'Unsupported CPU architecture.' >&2; exit 1 ;;
esac

rid="$platform-$architecture"
# The host directory is not versioned: config.toml, plugins/ and adapters/ survive SDK upgrades.
cache_dir=".shirobot-dev/host/$rid"
host_exe="$cache_dir/ShiroBot"
version_file="$cache_dir/.host-version"
archive="$cache_dir/shirobot-host-$rid-framework-dependent.zip"

if [ "$skip_build" = false ]; then
  dotnet build RemoteShell.csproj -c Debug -p:ShiroBotPluginPackagingEnabled=false -p:ShiroBotDevelopmentHostEnabled=false
fi

installed_version=$(cat "$version_file" 2>/dev/null || true)
if [ ! -f "$host_exe" ] || [ "$installed_version" != "$sdk_version" ]; then
  mkdir -p "$cache_dir"
  url="https://github.com/ShirokaProject/ShiroBot/releases/download/v$sdk_version/$(basename "$archive")"
  echo "Downloading ShiroBot v$sdk_version for $rid..."
  curl --fail --location --retry 2 "$url" --output "$archive"
  # The archive only contains the host executable, so the existing local data is kept.
  unzip -q -o "$archive" -d "$cache_dir"
  rm -f "$archive"
  chmod +x "$host_exe"
  printf '%s\n' "$sdk_version" > "$version_file"
fi

plugin_dir="$cache_dir/plugins/RemoteShell"
build_dir="bin/Debug/net10.0"
manifest="$plugin_dir/.shirobot-dev-files"
mkdir -p "$plugin_dir"
# Remove files copied by the previous run that the build no longer produces. Files the
# plugin or host created there (config.toml, data, .shirobot/native, ...) are never touched.
if [ -f "$manifest" ]; then
  while IFS= read -r file; do
    case "$file" in ""|*..*) continue ;; esac
    [ -e "$build_dir/$file" ] || rm -f "$plugin_dir/$file"
  done < "$manifest"
fi
# Keep the operator's config.toml across rebuilds; the shipped template is disabled.
find "$build_dir" -type f | while IFS= read -r source; do
  relative=${source#"$build_dir"/}
  if [ "$relative" = config.toml ] && [ -f "$plugin_dir/config.toml" ]; then continue; fi
  mkdir -p "$plugin_dir/$(dirname "$relative")"
  cp "$source" "$plugin_dir/$relative"
done
# config.toml is never listed, so a user-edited config is not deleted if the build stops emitting one.
(cd "$build_dir" && find . -type f ! -name config.toml | sed 's#^\./##') > "$manifest"

# Development uses a separate port from the standard container deployment.
if [ ! -f "$cache_dir/config.toml" ]; then
  printf 'protocols = []\n\n[api]\nenable = true\nlisten_urls = ["http://127.0.0.1:7004"]\n' > "$cache_dir/config.toml"
fi

if [ "$prepare_only" = true ]; then
  echo "ShiroBot v$sdk_version is ready. Use the Rider Run button to start it."
  exit 0
fi

echo "Starting ShiroBot v$sdk_version with RemoteShell..."
cd "$cache_dir"
exec ./ShiroBot "$@"
