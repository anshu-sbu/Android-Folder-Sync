#!/bin/bash
# Builds FolderSync.apk (Folder Sync Android, the phone app for Folder Sync PC) with the Android SDK tools directly (no Gradle needed).
# Usage: bash build.sh
set -euo pipefail
cd "$(dirname "$0")"

SDK="${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}"
SDK="$(cygpath -u "$SDK")"
BT="$SDK/build-tools/35.0.0"
JAR="$SDK/platforms/android-35/android.jar"
export JAVA_HOME="C:\\Program Files\\Android\\Android Studio\\jbr"
export PATH="/c/Program Files/Android/Android Studio/jbr/bin:$PATH"
export MSYS_NO_PATHCONV=1
# Signing: set FOLDERSYNC_KS (keystore path) and FOLDERSYNC_KS_PASS_FILE (file holding its password; alias "foldersync")
# for a release build. Without them the standard Android debug key is used.
if [ -n "${FOLDERSYNC_KS:-}" ]; then
  KS="$(cygpath -w "$FOLDERSYNC_KS")"; KS_ALIAS="foldersync"; KS_PASS="$(tr -d '\r\n' < "$FOLDERSYNC_KS_PASS_FILE")"
else
  KS="$(cygpath -w "$HOME/.android/debug.keystore")"; KS_ALIAS="androiddebugkey"; KS_PASS="android"
fi

rm -rf build && mkdir -p build/gen build/classes build/dex

echo "[1/6] resources"
"$BT/aapt2.exe" compile --dir res -o build/res.zip
"$BT/aapt2.exe" link -o build/base.apk -I "$(cygpath -w "$JAR")" \
  --manifest AndroidManifest.xml --java build/gen \
  --min-sdk-version 29 --target-sdk-version 34 \
  --version-code 1 --version-name 1.0 build/res.zip

echo "[2/6] compile"
javac -source 8 -target 8 -Xlint:-options -encoding UTF-8 \
  -bootclasspath "$(cygpath -w "$JAR");$(cygpath -w "$BT/core-lambda-stubs.jar")" -classpath "$(cygpath -w libs/zxing-core-3.5.3.jar)" -d build/classes \
  $(find src build/gen -name '*.java')

echo "[3/6] dex"
java -cp "$(cygpath -w "$BT/lib/d8.jar")" com.android.tools.r8.D8 --release --min-api 29 \
  --lib "$(cygpath -w "$JAR")" --output build/dex \
  $(find build/classes -name '*.class') libs/zxing-core-3.5.3.jar

echo "[4/6] package"
cp build/base.apk build/unsigned.apk
(cd build/dex && "$BT/aapt.exe" add ../unsigned.apk classes.dex >/dev/null)

echo "[5/6] align + sign"
"$BT/zipalign.exe" -f -p 4 build/unsigned.apk build/aligned.apk
APKSIGNER="$(cygpath -w "$BT/lib/apksigner.jar")"
java -jar "$APKSIGNER" sign --ks "$KS" --ks-key-alias "$KS_ALIAS" --ks-pass "pass:$KS_PASS" \
  --key-pass "pass:$KS_PASS" --out FolderSync.apk build/aligned.apk
java -jar "$APKSIGNER" verify --print-certs FolderSync.apk | head -1

echo "[6/6] built $(pwd)/FolderSync.apk ($(stat -c %s FolderSync.apk) bytes)"
