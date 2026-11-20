# Branding

`icon.svg` is the master Krakarr icon (a kraken on a rounded dark tile).
`icon-maskable.svg` is the full-bleed variant used for maskable/PWA and Apple touch icons.
`icon-1024.png` is a high-resolution export for READMEs, registries and stores.

The web UI icons in `web/public` are generated from these files:

| File | Source | Size |
| --- | --- | --- |
| `favicon.svg` | `icon.svg` (copy) | vector |
| `favicon.ico` | `icon.svg` | 16, 32, 48 |
| `icon-192.png`, `icon-512.png` | `icon.svg` | 192, 512 |
| `icon-maskable-512.png` | `icon-maskable.svg` | 512 |
| `apple-touch-icon.png` | `icon-maskable.svg` | 180 |

Any SVG rasteriser works (Inkscape, `rsvg-convert`, a headless browser); combine the 16/32/48 PNGs into the `.ico` with ImageMagick:

```bash
convert icon-16.png icon-32.png icon-48.png ../../web/public/favicon.ico
```
