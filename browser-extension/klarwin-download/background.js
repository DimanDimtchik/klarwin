const HOST = "de.ganzsoft.klarwin.download";

async function getProfileKey() {
  const { profileKey } = await chrome.storage.local.get({ profileKey: "" });
  return (profileKey || "").trim();
}

function sendNative(message) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(HOST, message, (response) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, message: chrome.runtime.lastError.message });
          return;
        }
        resolve(response || { ok: false, message: "Keine Antwort von KlarWin." });
      });
    } catch (err) {
      resolve({ ok: false, message: String(err) });
    }
  });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.get({ profileKey: null }, (data) => {
    if (data.profileKey === null || data.profileKey === undefined) {
      chrome.storage.local.set({ profileKey: "" });
    }
  });
});

chrome.downloads.onChanged.addListener(async (delta) => {
  if (!delta.state || delta.state.current !== "complete") return;

  try {
    const [item] = await chrome.downloads.search({ id: delta.id });
    if (!item || item.byExtensionId) return;
    if (!item.filename || item.filename.endsWith(".crdownload")) return;

    const profileKey = await getProfileKey();
    const response = await sendNative({
      action: "relocate",
      filePath: item.filename,
      url: item.url || "",
      referrer: item.referrer || "",
      profileKey
    });

    if (response?.moved) {
      chrome.notifications.create({
        type: "basic",
        iconUrl: "icons/icon128.png",
        title: "KlarWin",
        message: response.message || `Datei nach „${response.folder}“ verschoben.`
      });
    }
  } catch (_) {
    // ignore
  }
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.action === "ping") {
    sendNative({ action: "ping" }).then(sendResponse);
    return true;
  }
  if (msg?.action === "getRules") {
    sendNative({ action: "getRules" }).then(sendResponse);
    return true;
  }
  return false;
});
