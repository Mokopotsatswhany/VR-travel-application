# VR Travel Application

Unity VR travel experience built with Unity `6000.4.0f1`.

## Project Contents

This repository is intended to contain the Unity source project:

- `Assets/`
- `Packages/`
- `ProjectSettings/`

Generated folders such as `Library/`, `Temp/`, `Logs/`, and local IDE files are excluded from Git.

## Main Scene

- `Assets/Scenes/SampleScene.unity`

## Recommended Build Target

The project is currently set up closest to an Android / VR headset workflow because it uses:

- XR Interaction Toolkit
- XR Management
- OpenXR

WebGL can still be used for a browser demo, but Android is the safer option for the full VR experience.

## Opening The Project

1. Install Unity `6000.4.0f1`.
2. Open this folder in Unity Hub.
3. Load `Assets/Scenes/SampleScene.unity`.

## GitHub Publishing Notes

If you want a playable browser link later:

1. Build a WebGL version from Unity.
2. Put the WebGL output in a `docs/` folder.
3. Enable GitHub Pages from the `main` branch `/docs` folder.

For GitHub Pages, do not publish a WebGL build that only references `.br` files unless Unity Decompression Fallback is enabled. GitHub Pages does not add the Brotli `Content-Encoding` headers Unity expects.

If you want an Android share link:

1. Build an `APK`.
2. Upload it to a GitHub Release.
