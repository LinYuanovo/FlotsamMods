@echo off
rem Perf experiment: force Unity's D3D12 backend (the game ships D3D12\D3D12Core.dll).
rem D3D12 submits draw calls from worker threads, which offloads the render-submission
rem cost from the CPU main thread - the current bottleneck (perf overlay: ~5000 draw
rem calls, GPU wait 0ms).
rem Rollback: just launch Flotsam.exe normally. No other file is touched.
rem Verify it engaged: Player.log should say "Direct3D 12" instead of "Direct3D 11.0".
start "" "%~dp0Flotsam.exe" -force-d3d12
