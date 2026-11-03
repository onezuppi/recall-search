@echo off
rem Builds windows_embed_server.exe as an example of the Taconite EmbeddingGemma 2 crate.
rem Set TACONITE_DIR to the embeddinggemma2-windows-taconite folder, XRT_INCLUDE to the XRT headers
rem and VCVARS to vcvars64.bat of Visual Studio Build Tools. TARGET_DIR (optional) is the cargo output folder.
rem Nothing is read from fixed locations.
if "%TACONITE_DIR%"=="" echo TACONITE_DIR is not set & exit /b 1
if "%XRT_INCLUDE%"=="" echo XRT_INCLUDE is not set & exit /b 1
if "%VCVARS%"=="" echo VCVARS is not set & exit /b 1
call "%VCVARS%" >nul
set XRT_LIBDIR=%TACONITE_DIR%
if "%TARGET_DIR%"=="" set TARGET_DIR=%TACONITE_DIR%\target-recall
copy /y "%~dp0windows_embed_server.rs" "%TACONITE_DIR%\taconite-embeddinggemma2\examples\" >nul
cd /d "%TACONITE_DIR%\taconite-embeddinggemma2"
cargo build --release --example windows_embed_server --target-dir "%TARGET_DIR%" %*
