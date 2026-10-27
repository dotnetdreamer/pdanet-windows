@echo off
REM ===========================================================================
REM Rebuild the phone side agent dex from ProxyMain.java
REM
REM Needs two small tools in this folder (not stored in git, download once):
REM   ecj.jar
REM     https://repo1.maven.org/maven2/org/eclipse/jdt/ecj/3.26.0/ecj-3.26.0.jar
REM   android-9\lib\d8.jar  (Google d8 dexer, from Android build-tools r28)
REM     from https://dl.google.com/android/repository/build-tools_r28.0.3-windows.zip
REM     (extract android-9\lib\d8.jar into this folder)
REM
REM Needs a Java runtime on PATH (JRE 8 or newer). No Android SDK required.
REM ===========================================================================
setlocal
cd /d "%~dp0"

if not exist ecj.jar ( echo Missing ecj.jar, see the notes at the top of this file & exit /b 1 )
if not exist android-9\lib\d8.jar ( echo Missing android-9\lib\d8.jar, see the notes at the top of this file & exit /b 1 )

echo [1/3] Compiling ProxyMain.java with ECJ
java -jar ecj.jar -8 -nowarn -d classes ProxyMain.java || goto :err

echo [2/3] Converting to a dex with d8
if not exist dexout mkdir dexout
java -cp android-9\lib\d8.jar com.android.tools.r8.D8 --min-api 21 --output dexout ^
  classes\ProxyMain.class classes\ProxyMain$1.class classes\ProxyMain$2.class classes\ProxyMain$3.class || goto :err

echo [3/3] Copying the dex into the app resources
copy /y dexout\classes.dex ..\pdanet-windows\Resources\proxy.dex >nul || goto :err

echo Done. Now rebuild the app with: cd ..\pdanet-windows ^&^& dotnet publish -c Release
exit /b 0

:err
echo BUILD FAILED
exit /b 1
