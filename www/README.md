# www/ - Helide Website

Static Helide site. Contains a WebGL shader background, the landing page, and a duplicated image directory.

## Contents

- `background-lite.glsl`: WebGL fragment shader
- `images/`: screenshots / assets (see Warning below)

## Preview locally

```bash
python -m http.server 8000 # open `http://localhost:8000/`
```

## Warning: duplicated images

`www/images/` is a manual copy of `src/Assets/images/`. Before publishing or previewing, re-sync:

```bash
# From the repo root
cp -r src/Assets/images/* www/images/
```
