# Compila e instala PayloNative con las herramientas del SDK de Android (aapt2, d8, apksigner) y el JDK,
# sin Gradle ni descargas. Uso: ./build.ps1 [-Serial emulator-5554] [-NoInstall]
param(
    [string]$Serial,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$sdk = $env:ANDROID_HOME ?? $env:ANDROID_SDK_ROOT ?? 'C:\Program Files (x86)\Android\android-sdk'
$buildTools = Get-ChildItem "$sdk\build-tools" | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$androidJar = "$sdk\platforms\android-35\android.jar"
$bt = $buildTools.FullName

$root = $PSScriptRoot
$out = Join-Path $root 'build'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\gen", "$out\classes", "$out\dex" | Out-Null

Write-Host "build-tools $($buildTools.Name)"

# Recursos → R.java + APK sin código
& "$bt\aapt2.exe" compile --dir "$root\res" -o "$out\res.zip"
& "$bt\aapt2.exe" link -I $androidJar --manifest "$root\AndroidManifest.xml" `
    --java "$out\gen" -o "$out\app.unaligned.apk" "$out\res.zip"

# Java → .class → classes.dex
$sources = Get-ChildItem "$root\src", "$out\gen" -Recurse -Filter *.java | ForEach-Object FullName
& javac --release 17 -classpath $androidJar -d "$out\classes" @sources
$classes = Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName
& "$bt\d8.bat" --lib $androidJar --min-api 26 --output "$out\dex" @classes

# classes.dex dentro del APK, alinear y firmar con una clave de depuración local
& jar -u -M -f "$out\app.unaligned.apk" -C "$out\dex" classes.dex
& "$bt\zipalign.exe" -f 4 "$out\app.unaligned.apk" "$out\app.unsigned.apk"

# La misma clave en cada compilación: si cambia, adb no deja actualizar la app instalada.
$keystore = Join-Path $HOME '.android\debug.keystore'
if (-not (Test-Path $keystore)) {
    New-Item -ItemType Directory -Force (Split-Path $keystore) | Out-Null
    & keytool -genkeypair -keystore $keystore -storepass android -keypass android -alias androiddebugkey `
        -keyalg RSA -validity 10000 -dname 'CN=Android Debug,O=Android,C=US' 2>&1 | Out-Null
}
& "$bt\apksigner.bat" sign --ks $keystore --ks-pass pass:android --out "$out\PayloNative.apk" "$out\app.unsigned.apk"

Write-Host "APK: $out\PayloNative.apk"
if ($NoInstall) { return }

$adbArgs = if ($Serial) { @('-s', $Serial) } else { @() }
& adb @adbArgs install -r --no-incremental "$out\PayloNative.apk"
& adb @adbArgs shell am start -n com.driftcheck.paylonative/.SignUpActivity
