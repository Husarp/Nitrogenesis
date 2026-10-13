# Credits

## Sounds (`app/audio/`)

Both sounds are made by `tools/make_audio.py` from plain maths (sine waves and an envelope): no recording, no sample and no
file from anywhere else is used, so they belong to the project and carry no third-party license. Run
`python3 tools/make_audio.py` to make them again.

| File | What | Where it is used |
|---|---|---|
| `click.wav` | a 60 ms soft blip | every button press (Settings, "Interface sounds") |
| `engine_loop.wav` | one second of engine hum that loops without a seam | the followed or player car, with `pitch = 0.8 + 1.2 × speed / max speed` (Settings, "Engine sound"); silent above 5× speed |

## Pictures

The three car models (Hatchback, Racer, Truck) are 18 × 11 pixel drawings in `app/View/CarRenderer.cs`; the app icon is
`app/icon.png`. All are part of this project.

## Software

Nitrogenesis is made with [Godot Engine](https://godotengine.org) 4 (.NET), MIT license, and the .NET runtime, MIT license.
