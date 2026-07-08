const API = "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";
const FALLBACK = "https://github.com/ujjwalvivek/helide/releases/latest";

const versionEl = document.getElementById("version");
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

    if (tag) versionEl.textContent = "v" + tag;

    if (asset && asset.browser_download_url) {
      downloadEl.href = asset.browser_download_url;
      const size = formatSize(asset.size);
      btnLabel.textContent =
        "Download for Windows" + (size ? " - " + size : "");
    }
  } catch (err) {
    versionEl.textContent = "v1.0.0";
    downloadEl.href = FALLBACK;
    btnLabel.textContent = "Download for Windows";
  }
}

loadRelease();
