# Install and test RetroBridge from a terminal

These commands target an existing Jellyfin 12.4.0 server in the official Docker image. Run them in Bash or Zsh from the RetroBridge checkout. The build uses Git, .NET SDK 10.0.401 and Python 3. The live check uses curl and jq.

On a new machine, run `git clone https://github.com/seacloud9/jellyfin-retroarch-plugin.git` and `cd jellyfin-retroarch-plugin` first.

## Build main

```sh
(
  set -eu
  git switch main
  git pull --ff-only origin main
  export PATH="$HOME/.dotnet:$PATH"
  dotnet restore --locked-mode
  dotnet build -c Release --no-restore
  dotnet test -c Release --no-build
  python3 scripts/package-plugin.py
  cd artifacts
  shasum -a 256 -c "RetroBridge-$(python3 ../scripts/version.py).zip.sha256"
)
```

The last line should print the ZIP filename followed by `OK`. The two DLLs contain the plugin and game library. The archive also includes the redistribution notices.

## Install in a running Jellyfin container

Set `RB_CONTAINER` to the container you want to update. The example names the local playtest server. The install briefly stops that container. It backs up the current plugin under `artifacts/` and uses its existing installation directory, including versioned directories created by the catalog.

```sh
export RB_CONTAINER=retrobridge-playtest
(
  set -eu
  archive="artifacts/RetroBridge-$(python3 scripts/version.py).zip"
  stage=$(mktemp -d)
  trap 'rm -rf "$stage"' EXIT
  python3 -m zipfile -e "$archive" "$stage"
  chmod -R a+rX "$stage"

  installed=$(docker exec "$RB_CONTAINER" find /config/plugins -name Jellyfin.Plugin.RetroBridge.dll)
  case "$installed" in
    *$'\n'*) echo "Multiple RetroBridge installs found. Inspect these paths before updating:"; echo "$installed"; exit 1 ;;
    '') plugin_dir=/config/plugins/RetroBridge ;;
    *) plugin_dir=${installed%/*} ;;
  esac
  docker exec "$RB_CONTAINER" mkdir -p "$plugin_dir"
  backup=$(mktemp -d "$PWD/artifacts/retrobridge-backup.XXXXXX")
  docker cp "$RB_CONTAINER:$plugin_dir/." "$backup/"
  echo "Backup: $backup"
  echo "Install directory: $plugin_dir"

  docker stop "$RB_CONTAINER"
  docker cp "$stage/." "$RB_CONTAINER:$plugin_dir/"
  docker start "$RB_CONTAINER"
)
```

The server reads the new DLLs at startup. Its existing accounts, library settings and saves are outside the plugin directory. If copying an update fails after the stop, restore the previous installation with `docker cp "/path/to/backup/." "$RB_CONTAINER:/path/to/plugin/"` and `docker start "$RB_CONTAINER"`. Use the two paths printed above.

## Verify the live library on the local playtest server

The local test account is `Player` with an empty password. This block waits for Jellyfin, signs in, prints the titles available to that account, checks that all 45 have artwork, and signs out. The session token stays out of the output.

```sh
(
  set -euo pipefail
  url=http://127.0.0.1:8096
  curl -fsS --retry 45 --retry-delay 1 --retry-all-errors "$url/System/Info/Public" | jq -er '.Version'
  client="MediaBrowser Client=\"RetroBridge CLI\", Device=\"Terminal\", DeviceId=\"retrobridge-cli-$$\", Version=\"1\""
  token=$(curl -fsS -H "Authorization: $client" --json '{"Username":"Player","Pw":""}' \
    "$url/Users/AuthenticateByName" | jq -er '.AccessToken')
  auth="$client, Token=\"$token\""
  trap 'curl -fsS -X POST -H "Authorization: $auth" "$url/Sessions/Logout" >/dev/null' EXIT
  curl -fsS -H "Authorization: $auth" "$url/RetroBridge/Games" | jq -er '
    map(select(.IsBundled)) | sort_by(.Title) |
    if length == 45 and all(.[]; .HasArt)
    then "45 included titles with screenshots", .[].Title
    else error("Unexpected library size or missing artwork") end'
)
```

## Test every Play button headlessly

From the same checkout, run the following after building the ZIP. This starts a temporary Jellyfin container on a free local port, signs in through Jellyfin Web, checks the screenshots, launches every included cartridge, and returns to the library between games. It removes its test container when finished. This step needs Node.js, npm, Docker and network access for the browser and emulator dependency checks.

```sh
(
  set -eu
  (cd tests/browser && npm ci && npx playwright install chromium)
  python3 scripts/browser-play-smoke.py --port 0
)
```

On a minimal Linux host, use `npx playwright install --with-deps chromium` to install Chromium's system dependencies too. To play on the local Mac afterward, run `open http://127.0.0.1:8096/web/`, sign in as **Player**, and choose **Retro Games**.
