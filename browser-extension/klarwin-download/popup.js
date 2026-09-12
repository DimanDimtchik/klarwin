const statusEl = document.getElementById("status");
const rulesEl = document.getElementById("rules");
const input = document.getElementById("profileKey");

async function refresh() {
  const { profileKey } = await chrome.storage.local.get({ profileKey: "" });
  input.value = profileKey || "";

  chrome.runtime.sendMessage({ action: "ping" }, (ping) => {
    if (chrome.runtime.lastError) {
      statusEl.textContent = chrome.runtime.lastError.message;
      return;
    }
    if (!ping?.ok) {
      statusEl.textContent = "KlarWin nicht erreichbar: " + (ping?.message || "Native Messaging?");
      return;
    }
    statusEl.textContent = `KlarWin ${ping.version || ""} · Browser-Download ${ping.enabled ? "an" : "aus"}`;
  });

  chrome.runtime.sendMessage({ action: "getRules" }, (res) => {
    rulesEl.innerHTML = "";
    if (!res?.ok || !Array.isArray(res.rules)) return;
    for (const r of res.rules) {
      const li = document.createElement("li");
      const pk = r.profileKey ? ` · Profil „${r.profileKey}“` : "";
      li.textContent = `${r.hostPattern} → ${r.folderName || "?"}${pk}`;
      rulesEl.appendChild(li);
    }
  });
}

document.getElementById("save").addEventListener("click", async () => {
  await chrome.storage.local.set({ profileKey: input.value.trim() });
  statusEl.textContent = "Profil-Kennzeichen gespeichert.";
});

document.getElementById("ping").addEventListener("click", refresh);
refresh();
