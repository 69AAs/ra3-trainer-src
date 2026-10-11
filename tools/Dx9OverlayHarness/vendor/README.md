# HDE32 provenance

Unmodified decoder files from [MinHook v1.3.4](https://github.com/TsudaKageyu/minhook/tree/v1.3.4/src/hde): hde32.c, hde32.h, table32.h, pstdint.h. The upstream LICENSE.txt is included. Per-file SHA256 is in SHA256SUMS.txt and verified by build.ps1 before compiling.

Only the decoder is compiled. code-hook.h is this project's conservative relocation/patch/restore implementation, **not the MinHook hook engine**. Unsupported instructions fail closed. Relative CALL is intentionally rejected rather than silently changing call/pop or return-address semantics. The executable tests exercise the supported instruction semantics and rejected control flow.
