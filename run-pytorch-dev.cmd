@echo off
setlocal

set "CUDA_HOME=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8"
set "CUDAToolkit_ROOT=%CUDA_HOME%"
set "CUDA_TOOLKIT_ROOT_DIR=%CUDA_HOME%"
set "CUDA_PATH=%CUDA_HOME%"

set "PYTHON_HOME=C:\Users\posit\AppData\Local\Programs\Python\Python312"
set "PATH=C:\github\origin\pytorch\torch\lib;%CUDA_HOME%\bin;%CUDA_HOME%\extras\CUPTI\lib64;%PYTHON_HOME%;%PYTHON_HOME%\Scripts;%PATH%"

set "LIB=%CUDA_HOME%\lib\x64;%LIB%"
set "CMAKE_INCLUDE_PATH=%CUDA_HOME%\include"
set "TORCH_CUDA_ARCH_LIST=8.9"

cd /d C:\github\origin\pytorch
"%PYTHON_HOME%\python.exe" %*

