# Display resize runtime

KinaView uses the official Real-ESRGAN ncnn Vulkan Windows release, locally and only for static image enlargement. The compact `realesr-animevideov3` model targets anime artwork and supports 2x, 3x and 4x inference. Final sizing and reductions use the existing Magick.NET Lanczos implementation. Source images are never overwritten.

- Upstream: https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan
- Model documentation: https://github.com/xinntao/Real-ESRGAN/blob/master/docs/anime_video_model.md
- Archive: https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip
- Downloaded archive SHA-256: `abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d`
- Native executable SHA-256: `07e49f7cbb4ede01ae4dd4c399d3a7e5846e3d2085c3128eff881e55cb7b1a0c`
- Each x2/x3/x4 model `.bin` SHA-256: `548a36f9c3f4ab8da56cd3b13badf23968bee207b396dad14d04b830e5f2ab2d` (the shared weights use separate scale-specific `.param` files).

Only the executable, release OpenMP runtime, upstream Windows readme, and selected model files are extracted. These hashes pin the downloaded artifacts; they are not an upstream signature. `scripts/Install-ResizeModel.ps1` verifies the archive and extracts a fixed file list. Keep the license files in this directory when rebuilding the runtime.

Licenses included:

| Component | File |
|---|---|
| Real-ESRGAN ncnn Vulkan wrapper (MIT) | LICENSE.txt |
| Real-ESRGAN model/project (BSD-3-Clause) | MODEL-LICENSE.txt |
| ncnn | NCNN-LICENSE.txt |
| libwebp | LIBWEBP-LICENSE.txt |
| stb | STB-LICENSE.txt |
| dirent | DIRENT-LICENSE.txt |

The `vcomp140.dll` Microsoft OpenMP runtime is included in the upstream binary package and redistributed with the native executable. Microsoft Visual C++ runtime terms: https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/ .
