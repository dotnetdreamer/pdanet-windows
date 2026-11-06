@echo off
REM ===========================================================================
REM Rebuild pdanet-rtc.exe, the WebRTC helper behind screen sharing, from the Go code here
REM
REM Needs Go 1.26 or newer on PATH (https://go.dev/dl/). The libraries (Pion WebRTC and the
REM others in go.mod) come from the Go module proxy on the first build. No C compiler needed.
REM ===========================================================================
setlocal
cd /d "%~dp0"

where go >nul 2>nul || ( echo Go is not on PATH, see the notes at the top of this file & exit /b 1 )

echo [1/3] Checking the code
go vet ./... || goto :err
go test ./... || goto :err

echo [2/3] Building a small Windows executable
set CGO_ENABLED=0
set GOOS=windows
set GOARCH=amd64
go build -trimpath -buildvcs=false -ldflags "-s -w" -o pdanet-rtc.exe . || goto :err

echo [3/3] Testing it and copying it into the app resources
.\pdanet-rtc.exe selftest || goto :err
if not exist ..\pdanet-windows\Resources\rtc mkdir ..\pdanet-windows\Resources\rtc
copy /y pdanet-rtc.exe ..\pdanet-windows\Resources\rtc\pdanet-rtc.exe >nul || goto :err
del pdanet-rtc.exe

echo Done. Now rebuild the app with: cd ..\pdanet-windows ^&^& dotnet publish -c Release
exit /b 0

:err
echo BUILD FAILED
exit /b 1
