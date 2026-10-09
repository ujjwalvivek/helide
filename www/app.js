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
    versionEls.forEach(el => el.textContent = "v1.3.2");
    if (downloadEl) downloadEl.href = FALLBACK;
    if (btnLabel) btnLabel.textContent = "Download for Windows";
  }
}

loadRelease();

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
