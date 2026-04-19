# PyTorch Windows CUDA Build Notes

Working setup for building PyTorch from source on Windows:

## Environment

- Visual Studio 2022 Developer Command Prompt
- CUDA Toolkit 12.8
- Python 3.12
- `pip install --no-build-isolation -v -e .`

## Confirmed good toolchain

```cmd
where cl
cl
where nvcc
nvcc --version
echo %CUDA_PATH%
```

Expected results:

- `cl.exe` from `C:\Program Files\Microsoft Visual Studio\2022\Community\...`
- `nvcc.exe` from `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin`
- `CUDA_PATH` set to `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8`

## Recommended prompt

Use the x64-hosted VS 2022 developer prompt:

```cmd
"%ProgramFiles%\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
```

From `cmd.exe`, call it like this:

```cmd
call "%ProgramFiles%\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
```

## CUDA environment

Set these before building:

```cmd
set CUDA_HOME=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
set CUDAToolkit_ROOT=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
set CUDA_TOOLKIT_ROOT_DIR=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
set CUDA_PATH=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
set PATH=%CUDA_HOME%\bin;%PATH%
set LIB=%CUDA_HOME%\lib\x64;%LIB%
set CMAKE_INCLUDE_PATH=%CUDA_HOME%\include
set TORCH_CUDA_ARCH_LIST=8.9
```

## Build command

```cmd
cd C:\github\origin\pytorch
python -m pip install --no-build-isolation -v -e .
```

## Rebuild from `cmd.exe`

```cmd
call "%ProgramFiles%\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
set CUDA_HOME=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
set CUDAToolkit_ROOT=%CUDA_HOME%
set CUDA_TOOLKIT_ROOT_DIR=%CUDA_HOME%
set CUDA_PATH=%CUDA_HOME%
set PATH=C:\github\origin\pytorch\torch\lib;%CUDA_HOME%\bin;%CUDA_HOME%\extras\CUPTI\lib64;%PATH%
set LIB=%CUDA_HOME%\lib\x64;%LIB%
set CMAKE_INCLUDE_PATH=%CUDA_HOME%\include
set TORCH_CUDA_ARCH_LIST=8.9

cd C:\github\origin\pytorch
python -m pip install --no-build-isolation -v -e .
```

## Issues encountered and fixes

- `Cannot import 'setuptools.build_meta'`
  - Fix: install `setuptools` in the active environment.
- CMake found CUDA 13.2 but could not use it
  - Fix: switch to CUDA 12.8.
- `CMAKE_CUDA_ARCHITECTURES must be non-empty if set`
  - Fix: remove stale `build` cache and set `TORCH_CUDA_ARCH_LIST=8.9`.
- CUDA rejected VS 2026 / `18.x`
  - Fix: use VS 2022 instead.
- Linker failure in editable build
  - Fix: use the x64-hosted VS 2022 developer prompt.

## Successful result

The build completed successfully and installed:

```text
torch-2.13.0a0+gitfadf0e3
```
