# Howl SDK

Zero-cost gameplay event emitter for Unity, feeding an Overwolf overlay.

## Install

Package Manager → **+** → **Install package from git URL…**

```
https://github.com/devomage/howl-sdk.git?path=/com.focus.howl.installer
```

The installer checks your setup, installs the core package and opens **Tools → Howl → SDK Manager**, which handles integrations (Opsive UCC) and updates. The installer then removes itself.

## Packages

| Package | Contents |
|---|---|
| `com.focus.howl.installer` | Bootstrapper (Editor only) |
| `com.focus.howl` | Emitter runtime + Howl SDK Manager |
| `com.focus.howl.opsive` | Opsive Ultimate Character Controller 3.3+ integration |

All packages are versioned together and released as one tag (`v0.1.0`).
