const API = "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";
const FALLBACK = "https://github.com/ujjwalvivek/helide/releases/latest";

const versionEls = document.querySelectorAll("#version");
const downloadEl = document.getElementById("download");
const btnLabel = document.getElementById("btn-label");

function formatSize(bytes) {
  if (!bytes) return null;
  const mb = bytes / 1048576;
  return mb >= 1024
    ? (mb / 1024).toFixed(1) + " GB"
    : (mb >= 1 ? mb.toFixed(1) : (bytes / 1024).toFixed(0)) + " MB";
}

async function loadRelease() {
  try {
    const res = await fetch(API, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!res.ok) throw new Error("release not found");

    const release = await res.json();
    const tag = (release.tag_name || "").replace(/^v/, "");
    const asset = (release.assets || []).find(
      (a) => a.name === "helide-win-x64.zip",
    );

    if (tag) versionEls.forEach(el => el.textContent = "v" + tag);

    if (asset && asset.browser_download_url) {
      if (downloadEl) downloadEl.href = asset.browser_download_url;
      const size = formatSize(asset.size);
      if (btnLabel) btnLabel.textContent =
        "Download for Windows" + (size ? " - " + size : "");
    }
  } catch (err) {
    versionEls.forEach(el => el.textContent = "v1.6.2");
    if (downloadEl) downloadEl.href = FALLBACK;
    if (btnLabel) btnLabel.textContent = "Download for Windows";
  }
}

loadRelease();

// Screenshots carousel
const SHOTS = [
  { id: "welcome", label: "welcome" },
  { id: "workspace", label: "workspace" },
  { id: "filetree", label: "file tree" },
  { id: "palette", label: "palette" },
  { id: "zen", label: "zen mode" },
];

const carousel = document.getElementById("carousel");
const track = document.getElementById("carousel-track");
const dotsBox = document.getElementById("carousel-dots");
const lightbox = document.getElementById("lightbox");

if (carousel && track && dotsBox && lightbox && SHOTS.length) {
  const thumb = id => "images/thumbnails/" + id + "-640x360.webp";
  const full = id => "images/webp/" + id + "-1920x1080.webp";

  const SPEED = 0.014; // px per ms
  const reduceMotion =
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // Below 720px the sidebar stacks full width, so the reel scrolls sideways
  // instead of downwards. This mirrors the CSS breakpoint.
  const axisQuery = window.matchMedia("(max-width: 720px)");
  let horizontal = axisQuery.matches;

  let offset = 0;
  let stride = 0;
  let last = 0;
  let active = -1;
  let current = 0;
  let lastFocus = null;
  let hovering = false;
  let gliding = 0;
  let lightboxOpen = false;

  const slides = [];

  // Keeps the track offset in [-loop, 0) so a full period is one loop wide.
  function wrap(v, loop) {
    if (!(loop > 0)) return 0;
    return ((v % loop) + loop) % loop - loop;
  }

  function place() {
    track.style.transform = horizontal
      ? "translate3d(" + offset + "px,0,0)"
      : "translate3d(0," + offset + "px,0)";
  }

  function makeSlide(shot, index, hidden) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "carousel-slide";
    if (hidden) btn.setAttribute("aria-hidden", "true");
    btn.tabIndex = hidden ? -1 : 0;
    btn.dataset.index = index;

    const img = document.createElement("img");
    img.src = thumb(shot.id);
    img.alt = shot.label;
    // The reel duplicates every slide, so lazy images leave blank frames until
    // the transform scrolls them near the viewport. The set is tiny, so load it
    // eagerly to keep the loop seamless from the first frame.
    img.loading = "eager";
    img.decoding = "sync";
    img.draggable = false;

    const cap = document.createElement("span");
    cap.className = "slide-caption";
    cap.textContent = shot.label;

    btn.append(img, cap);
    return btn;
  }

  function build() {
    const frag = document.createDocumentFragment();
    slides.length = 0;

    SHOTS.forEach(shot => {
      const pre = new Image();
      pre.src = thumb(shot.id);
    });

    SHOTS.forEach((shot, i) => {
      const btn = makeSlide(shot, i, false);
      slides.push(btn);
      frag.appendChild(btn);
    });

    track.appendChild(frag);
    ensureCopies();

    dotsBox.innerHTML = "";
    SHOTS.forEach((shot, i) => {
      const dot = document.createElement("button");
      dot.type = "button";
      dot.className = "dot";
      dot.setAttribute("role", "tab");
      dot.setAttribute("aria-label", shot.label);
      dot.setAttribute("aria-selected", i === 0 ? "true" : "false");
      dot.addEventListener("click", () => goTo(i));
      dotsBox.appendChild(dot);
    });
  }

  // The offset wraps once per period (loop = stride * SHOTS.length). To keep the
  // loop seamless the track must stay at least one period taller than the
  // viewport, otherwise wrapping exposes an empty band. Append hidden copies
  // until that holds, with a cap so a huge viewport can't grow forever.
  function ensureCopies() {
    if (!stride) {
      // stride is not measured yet; add one copy so measure() can run.
      if (!track.children.length || track.children.length < SHOTS.length * 2) {
        const frag = document.createDocumentFragment();
        SHOTS.forEach((shot, i) => frag.appendChild(makeSlide(shot, i, true)));
        track.appendChild(frag);
      }
      return;
    }
    const loop = stride * SHOTS.length;
    const viewport = horizontal ? carousel.clientWidth : carousel.clientHeight;
    const need = loop + viewport + stride;
    const copies = Math.ceil(track.children.length / SHOTS.length);
    const maxCopies = Math.min(24, Math.ceil(need / loop) + 2);
    if (copies < maxCopies) {
      const frag = document.createDocumentFragment();
      for (let c = copies; c < maxCopies; c++) {
        SHOTS.forEach((shot, i) => frag.appendChild(makeSlide(shot, i, true)));
      }
      track.appendChild(frag);
    }
  }

  function measure() {
    if (!slides.length) return;
    const first = slides[0];
    const next = slides[1];
    const gap = parseFloat(
      getComputedStyle(track)[horizontal ? "columnGap" : "rowGap"]
    ) || 0;
    stride = next
      ? (horizontal
          ? first.offsetWidth +
            (next.getBoundingClientRect().left - first.getBoundingClientRect().right)
          : first.offsetHeight +
            (next.getBoundingClientRect().top - first.getBoundingClientRect().bottom))
      : (horizontal ? first.offsetWidth : first.offsetHeight) + gap;
    ensureCopies();
    offset = wrap(offset, stride * SHOTS.length);
    place();
  }

  // Eases the track to whichever copy of slide i sits nearest the viewport,
  // so a dot click never jumps a whole period.
  function goTo(i) {
    current = ((i % SHOTS.length) + SHOTS.length) % SHOTS.length;
    const loop = stride * SHOTS.length;
    const box = horizontal ? carousel.clientWidth : carousel.clientHeight;
    const itemSize = horizontal
      ? slides[current].offsetWidth
      : slides[current].offsetHeight;
    const target = -(current * stride) + (box - itemSize) / 2;
    offset = wrap(target + Math.round((offset - target) / loop) * loop, loop);

    if (reduceMotion) {
      place();
    } else {
      gliding = performance.now() + 420;
      track.classList.add("is-gliding");
      place();
    }
    setActive(current);
  }

  function setActive(i) {
    if (i === active) return;
    active = i;
    const dots = dotsBox.children;
    for (let k = 0; k < dots.length; k++) {
      dots[k].setAttribute("aria-selected", k === i ? "true" : "false");
    }
  }

  function nearestToCenter() {
    const boxRect = carousel.getBoundingClientRect();
    const mid = horizontal ? boxRect.left + boxRect.width / 2 : boxRect.top + boxRect.height / 2;
    let best = 0;
    let bestDist = Infinity;
    for (let k = 0; k < slides.length; k++) {
      const r = slides[k].getBoundingClientRect();
      const c = horizontal ? r.left + r.width / 2 : r.top + r.height / 2;
      const d = Math.abs(c - mid);
      if (d < bestDist) {
        bestDist = d;
        best = slides[k].dataset.index | 0;
      }
    }
    return best;
  }

  function tick(now) {
    if (!last) last = now;
    const dt = Math.min(now - last, 100);
    last = now;

    if (gliding && now > gliding) {
      gliding = 0;
      track.classList.remove("is-gliding");
    }

    const frozen = reduceMotion || hovering || lightboxOpen || gliding || document.hidden;
    if (!frozen && stride > 0) {
      offset = wrap(offset - dt * SPEED, stride * SHOTS.length);
      place();
      setActive(nearestToCenter());
    }
    requestAnimationFrame(tick);
  }

  track.addEventListener("click", e => {
    const slide = e.target.closest(".carousel-slide");
    if (slide) open(slide.dataset.index | 0);
  });

  carousel.addEventListener("mouseenter", () => {
    hovering = true;
  });
  carousel.addEventListener("mouseleave", () => {
    hovering = false;
  });

  let resizeTimer;
  function scheduleMeasure() {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(measure, 120);
  }

  window.addEventListener("resize", scheduleMeasure);

  const onAxisChange = () => {
    horizontal = axisQuery.matches;
    scheduleMeasure();
  };
  if (axisQuery.addEventListener) axisQuery.addEventListener("change", onAxisChange);
  else axisQuery.addListener(onAxisChange);

  // Lightbox
  const lbImg = document.getElementById("lightbox-img");
  const lbLabel = document.getElementById("lightbox-label");
  const lbIndex = document.getElementById("lightbox-index");

  function preload(i) {
    const shot = SHOTS[((i % SHOTS.length) + SHOTS.length) % SHOTS.length];
    const img = new Image();
    img.src = full(shot.id);
  }

  function open(i) {
    if (lightboxOpen) return;
    lightboxOpen = true;
    current = ((i % SHOTS.length) + SHOTS.length) % SHOTS.length;
    lastFocus = document.activeElement;
    render();
    preload(current + 1);
    preload(current - 1);
    lightbox.hidden = false;
    document.body.style.overflow = "hidden";
    document.getElementById("lightbox-close").focus();
  }

  function render() {
    const shot = SHOTS[current];
    lbImg.src = full(shot.id);
    lbImg.alt = shot.label;
    lbLabel.textContent = shot.label;
    lbIndex.textContent = (current + 1) + " / " + SHOTS.length;
  }

  function step(dir) {
    current = (current + dir + SHOTS.length) % SHOTS.length;
    render();
    preload(current + dir);
    goTo(current);
  }

  function close() {
    if (!lightboxOpen) return;
    lightboxOpen = false;
    lightbox.hidden = true;
    document.body.style.overflow = "";
    lbImg.src = "";
    if (lastFocus && lastFocus.focus) lastFocus.focus();
    goTo(current);
  }

  lightbox.addEventListener("click", e => {
    if (e.target.hasAttribute("data-lightbox-close")) close();
    else if (e.target.id === "lightbox-prev") step(-1);
    else if (e.target.id === "lightbox-next") step(1);
    else if (e.target.id === "lightbox-close") close();
  });

  document.addEventListener("keydown", e => {
    if (!lightboxOpen) return;
    if (e.key === "Escape") close();
    else if (e.key === "ArrowLeft") step(-1);
    else if (e.key === "ArrowRight") step(1);
    else if (e.key === "Tab") e.preventDefault();
  });

  build();
  requestAnimationFrame(() => {
    measure();
    requestAnimationFrame(tick);
  });
}

// Background shader
const bgCanvas = document.getElementById("bg-shader");
if (bgCanvas && bgCanvas.getContext) {
  const gl = bgCanvas.getContext("webgl") || bgCanvas.getContext("experimental-webgl");
  if (gl) {
    const vsSource = `
      attribute vec2 a_pos;
      void main() {
        gl_Position = vec4(a_pos, 0, 1);
      }
    `;
    const fsSourcePlaceholder = `
      precision mediump float;
      uniform float iTime;
      uniform vec2 iResolution;
      void main() {
        gl_FragColor = vec4(0.05, 0.06, 0.09, 1.0);
      }
    `;

    function compile(type, src) {
      const sh = gl.createShader(type);
      gl.shaderSource(sh, src);
      gl.compileShader(sh);
      return sh;
    }

        const shaderHeader = `
precision mediump float;
uniform float iTime;
uniform vec2 iResolution;
uniform vec2 iMouse;
`;

    fetch("background-lite.glsl")
      .then(r => r.text())
      .then(src => {
        const vs = compile(gl.VERTEX_SHADER, vsSource);
        const fs = compile(gl.FRAGMENT_SHADER, shaderHeader + src);
        const prog = gl.createProgram();
        gl.attachShader(prog, vs);
        gl.attachShader(prog, fs);
        gl.linkProgram(prog);
        gl.useProgram(prog);

        const posLoc = gl.getAttribLocation(prog, "a_pos");
        const buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(posLoc);
        gl.vertexAttribPointer(posLoc, 2, gl.FLOAT, false, 0, 0);

        const iTime = gl.getUniformLocation(prog, "iTime");
        const iRes = gl.getUniformLocation(prog, "iResolution");
        const iMouse = gl.getUniformLocation(prog, "iMouse");

        function resize() {
          const rect = bgCanvas.parentElement.getBoundingClientRect();
          bgCanvas.width = rect.width;
          bgCanvas.height = rect.height;
          gl.viewport(0, 0, bgCanvas.width, bgCanvas.height);
        }
        resize();
        window.addEventListener("resize", resize);

        let mouse = [0, 0];
        bgCanvas.addEventListener("mousemove", e => {
          const rect = bgCanvas.getBoundingClientRect();
          mouse[0] = (e.clientX - rect.left) / rect.width;
          mouse[1] = 1.0 - (e.clientY - rect.top) / rect.height;
        });

        let start = Date.now();
        function draw() {
          const t = (Date.now() - start) / 1000;
          gl.uniform1f(iTime, t);
          gl.uniform2f(iRes, bgCanvas.width, bgCanvas.height);
          gl.uniform2f(iMouse, mouse[0], mouse[1]);
          gl.drawArrays(gl.TRIANGLES, 0, 3);
          requestAnimationFrame(draw);
        }
        draw();
      })
      .catch(err => console.warn("Shader load failed:", err));
  }
}
