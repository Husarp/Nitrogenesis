# Milestone notes

## M0 — Setup (2026-10-04)

- Pinned tools (build/versions.txt): Godot 4.7.2-stable .NET (editor + export templates, SHA-512 checked) in
  ~/tools/godot-4.7.2 and ~/.local/share/godot/export_templates/4.7.2.stable.mono; .NET SDK 10.0.401 in ~/.dotnet;
  rcedit 2.0.0 in ~/tools/rcedit; Wine 10.0 (apt) with prefix ~/.wine-nitrogenesis.
- Build: `build/export-windows.sh` → out/Nitrogenesis-X.Y.Z-win64.zip (72 MB: exe with embedded pck + data_ folder
  with the .NET runtime). It writes VERSION into project.godot and export_presets.cfg, runs the Sim tests, fails on
  any export ERROR, and stamps icon + version with rcedit.
- Checked on Linux: tests green; exe starts under Wine headless (.NET module loads, no errors); version resource
  0.1.0.0 / Nitrogenesis / Husarp.
- Placeholder icon (orange N with speed streaks) in app/icon.png/.ico; final look in M7.

What to try on the laptop: unzip, run Nitrogenesis.exe → dark window "NITROGENESIS", version line, FPS ≈ 60 (cap 60);
right-click the exe → Properties → Details shows version 0.1.0.0 and the icon.
