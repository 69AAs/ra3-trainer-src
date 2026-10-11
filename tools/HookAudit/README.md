# HookAudit (offline only)

Run from the repository root with the .NET 8 SDK:

```powershell
dotnet run --project tools/HookAudit -c Release -- . --verify-current
dotnet run --project tools/HookAudit -c Release -- . --verify-original
dotnet run --project tools/HookAudit -c Release -- . --inspect --sample
```

The first argument is the repository directory. The round15 evidence directory must contain one full-memory minidump and one evidence zip. The tool reads Memory64List, PE code sections and the zip's `trainer/hooks.json`. It restores ExpectedHex only in its local image, never writes the input files and never opens a process. This evidence adapter intentionally targets this x86 RA3 snapshot (base 00400000, .text RVA 1000, size 7C6000); it is not a general minidump debugger. Missing executable dump bytes fail closed.

`--verify-current` returns 1 for a current manifest mismatch/internal entry. `--verify-original` returns 1 for the old vulnerable manifest (expected for this evidence). Without either option it produces evidence rather than treating the expected old defect as an execution error. Reports go to docs/round15-audit.

The reusable check is `HookEntryAudit.Verify(byte[] code, uint imageAddress, uint site, int patchLength)`. Give it complete code regions beginning on actual instruction boundaries. It detects direct near jmp/jcc/call/loop targets; indirect runtime-computed targets require further analysis. The production test uses the complete restored .text fixture and unconditionally checks every production hook, expected bytes and region coverage. Missing fixture fails. It does not quietly skip when the 2.5GB dump is absent.

Inspector options: `0xRVA` disassembles 0x500 bytes; `words=ABSOLUTE_HEX` dumps 0x100 bytes; `xref=ABSOLUTE_HEX` searches code references; `memfind=VALUE_HEX` searches aligned dwords across dump memory; `--sample` runs the production read-only sampler through a dump-only adapter. Adapter page readability means captured bytes exist, not proof of live page protection.

See docs/round15-findings.md for the evidence chain, fixture hash, corrected hooks and static-analysis limits.

Round 17: `./tools/HookAudit/round17.ps1 -Dotnet D:/dotnet/dotnet.exe` reproduces the coordinate, dispatcher and terrain evidence. `--inspect --round17` writes to `docs/round17-audit` and encodes the production AA blocks starting at MustCode+600 locally with explicitly documented synthetic allocation bases. `bootstrap.txt` is generated code, not captured remote memory. It never installs or executes that code. The named disassembly windows and findings distinguish verified instructions from incomplete API semantics.

Round 18: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/HookAudit/round18-source-evidence.ps1` checks the supplied original AA against the decrypted JSON script, lists the separate Free Build script and all manifest hooks, and extracts exact bytes from the offline `.text` fixture. `dotnet run --project tools/HookAudit -c Release -- . --inspect --round18 0x205240` writes isolated evidence to `docs/round18-audit`, including exact per-source-block generated instructions in `bootstrap.txt`. This mode uses the existing local minidump and never opens a process. It preserves round15/round17 evidence.
