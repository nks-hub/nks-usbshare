// NKS UsbShare control panel.
// One WebSocket carries the whole node state; every render is a full redraw from that snapshot,
// which keeps the page honest about what the daemon actually thinks is true.

const $ = (id) => document.getElementById(id);

let state = null;
let expandedHost = null;
let remoteDevices = new Map(); // "address:port" -> device list

// ---------- transport ----------

async function post(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body ?? {}),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok || payload.ok === false) throw new Error(payload.error || `${path} failed`);
  return payload;
}

function connect() {
  const socket = new WebSocket(`ws://${location.host}/api/events`);
  socket.onmessage = (event) => render(JSON.parse(event.data));
  socket.onclose = () => {
    // The daemon stopped or the machine slept; keep trying quietly.
    setTimeout(connect, 1500);
  };
}

async function refresh() {
  const response = await fetch("/api/state");
  render(await response.json());
}

// ---------- rendering ----------

function render(next) {
  state = next;

  $("node-name").textContent = state.hostName;
  $("node-platform").textContent = `${state.platform} · ${state.backend}`;
  $("link-port").textContent = state.hostRunning ? state.linkPort : `${state.linkPort} (idle)`;
  $("fingerprint").textContent = state.fingerprint;
  $("token").textContent = state.pairingToken;

  $("addresses").innerHTML = state.localAddresses.length
    ? state.localAddresses.map((a) => `<span>${escapeHtml(a)}</span>`).join("")
    : "<span>no network interface</span>";

  const signal = $("signal");
  signal.classList.toggle("live", state.hostRunning);
  $("signal-text").textContent = state.hostRunning ? "sharing" : "not sharing";

  renderLocalDevices();
  renderHosts();
  renderImports();
  renderClients();
  renderLog();
}

function renderLocalDevices() {
  const container = $("local-devices");
  container.innerHTML = "";

  const devices = state.devices;
  $("local-count").textContent = devices.filter((d) => d.shared && d.shareEnabled).length;
  $("local-empty").hidden = devices.length > 0;

  for (const device of devices) {
    const row = buildRow(device.deviceId, device.name, deviceMeta(device), deviceSpec(device));
    row.classList.toggle("on", device.shared && device.shareEnabled);

    const action = row.querySelector(".action");

    if (device.blockedReason) {
      const note = document.createElement("div");
      note.className = "blocked";
      note.textContent = device.blockedReason;
      action.appendChild(note);
    } else {
      const button = document.createElement("button");
      button.className = "pill" + (device.shared && device.shareEnabled ? " armed" : "");
      button.textContent = device.shared && device.shareEnabled ? "shared" : "share";
      button.onclick = () => guard(async () => {
        if (device.shared && device.shareEnabled) {
          await post("/api/unshare", { deviceId: device.deviceId });
          toast(`${device.name} is no longer shared`);
        } else {
          await post("/api/share", { deviceId: device.deviceId });
          toast(`${device.name} is now shared`);
        }
        await refresh();
      });
      action.appendChild(button);
    }

    container.appendChild(row);
  }
}

function renderHosts() {
  const container = $("hosts");
  container.innerHTML = "";

  if (!state.remoteHosts.length) {
    container.innerHTML = `<p class="empty">Pair with a host below to see the devices it offers.</p>`;
    return;
  }

  for (const host of state.remoteHosts) {
    const key = `${host.address}:${host.port}`;

    const element = document.createElement("div");
    element.className = "host" + (host.connected ? " online" : "");
    element.innerHTML = `
      <span class="dot"></span>
      <div class="who">
        <b>${escapeHtml(host.name)}</b>
        <span>${escapeHtml(key)}${host.fingerprint ? ` · ${escapeHtml(host.fingerprint)}` : " · not paired yet"}</span>
      </div>`;

    const browse = document.createElement("button");
    browse.className = "pill pull";
    browse.textContent = expandedHost === key ? "hide" : "browse";
    browse.onclick = () => guard(async () => {
      if (expandedHost === key) {
        expandedHost = null;
        renderHosts();
        return;
      }
      const result = await post("/api/hosts/devices", { address: host.address, port: host.port });
      remoteDevices.set(key, result.devices);
      expandedHost = key;
      renderHosts();
    });

    const forget = document.createElement("button");
    forget.className = "pill drop";
    forget.textContent = "forget";
    forget.onclick = () => guard(async () => {
      await post("/api/hosts/remove", { address: host.address, port: host.port });
      remoteDevices.delete(key);
      if (expandedHost === key) expandedHost = null;
      toast(`Forgot ${key}`);
      await refresh();
    });

    element.append(browse, forget);
    container.appendChild(element);

    if (expandedHost === key) {
      for (const device of remoteDevices.get(key) ?? []) {
        const row = buildRow(
          device.deviceId,
          device.name,
          [device.hardwareKey, device.manufacturer, device.serialNumber].filter(Boolean).join(" · "),
          `${device.kind} · ${device.speed}`);

        const button = document.createElement("button");
        button.className = "pill pull";
        button.disabled = device.imported;
        button.textContent = device.imported ? "imported" : "import";
        button.onclick = () => guard(async () => {
          await post("/api/import", { address: host.address, port: host.port, deviceId: device.deviceId });
          toast(`Imported ${device.name}`);
          const result = await post("/api/hosts/devices", { address: host.address, port: host.port });
          remoteDevices.set(key, result.devices);
          renderHosts();
          await refresh();
        });

        row.querySelector(".action").appendChild(button);
        container.appendChild(row);
      }

      if (!(remoteDevices.get(key) ?? []).length) {
        const empty = document.createElement("p");
        empty.className = "empty";
        empty.textContent = "This host is not offering any device to you.";
        container.appendChild(empty);
      }
    }
  }
}

function renderImports() {
  const container = $("imports");
  container.innerHTML = "";
  $("import-count").textContent = state.imports.length;

  for (const item of state.imports) {
    const row = buildRow(item.deviceId, item.name, `from ${item.hostName} · ${item.hostAddress}`, item.speed);

    const button = document.createElement("button");
    button.className = "pill drop";
    button.textContent = "release";
    button.onclick = () => guard(async () => {
      await post("/api/release", { key: item.key });
      toast(`Released ${item.name}`);
      if (expandedHost) {
        const [address, port] = expandedHost.split(":");
        const result = await post("/api/hosts/devices", { address, port: Number(port) });
        remoteDevices.set(expandedHost, result.devices);
      }
      await refresh();
    });

    row.querySelector(".action").appendChild(button);
    container.appendChild(row);
  }
}

function renderClients() {
  const panel = $("clients-panel");
  panel.hidden = state.sessions.length === 0;
  $("client-count").textContent = state.sessions.length;

  const container = $("clients");
  container.innerHTML = "";

  for (const session of state.sessions) {
    const held = session.openDevices.length;
    const row = buildRow(
      session.clientName,
      session.clientName,
      `${session.remoteEndPoint ?? "unknown"}${session.fingerprint ? ` · ${session.fingerprint}` : ""}`,
      held === 1 ? "1 device open" : `${held} devices open`);
    container.appendChild(row);
  }
}

function renderLog() {
  const container = $("log");
  container.innerHTML = state.log.map((entry) => `
    <div class="line ${entry.level}">
      <time>${escapeHtml(entry.time)}</time>
      <span class="cat">${escapeHtml(entry.category)}</span>
      <span class="msg">${escapeHtml(entry.message)}</span>
    </div>`).join("");

  const last = state.log[state.log.length - 1];
  $("console-last").textContent = last ? last.message : "";

  if ($("console").classList.contains("open")) container.scrollTop = container.scrollHeight;
}

// ---------- small builders ----------

function buildRow(portLabel, name, meta, spec) {
  const row = $("device-row").content.cloneNode(true).querySelector(".row");
  row.querySelector(".port").textContent = shortPort(portLabel);
  row.querySelector(".name").textContent = name;
  row.querySelector(".meta").textContent = meta;
  row.querySelector(".spec").textContent = spec;
  return row;
}

// Device ids are long and machine shaped; the last meaningful chunk is what a human recognises.
function shortPort(id) {
  const trimmed = String(id).replace(/[\\/]+$/, "");
  const tail = trimmed.split(/[\\/:|]/).filter(Boolean).pop() ?? trimmed;
  return tail.length > 5 ? tail.slice(-5) : tail;
}

function deviceMeta(device) {
  return [device.hardwareKey, device.manufacturer, device.serialNumber, device.busPath]
    .filter((part) => part && part.length)
    .join(" · ");
}

function deviceSpec(device) {
  const parts = [device.deviceKind, device.speed];
  if (device.interfaceCount) parts.push(`${device.interfaceCount} if / ${device.endpointCount} ep`);
  return parts.join(" · ");
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// ---------- feedback ----------

let toastTimer = null;

function toast(message, bad = false) {
  const element = $("toast");
  element.textContent = message;
  element.classList.toggle("bad", bad);
  element.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.remove("show"), bad ? 6000 : 2600);
}

async function guard(action) {
  try {
    await action();
  } catch (error) {
    toast(error.message, true);
  }
}

async function copy(text, what) {
  try {
    await navigator.clipboard.writeText(text);
    toast(`${what} copied`);
  } catch {
    toast("Clipboard is not available in this browser", true);
  }
}

// ---------- wiring ----------

$("host-toggle").onclick = () => guard(async () => {
  await post(state.hostRunning ? "/api/host/stop" : "/api/host/start");
  await refresh();
});

$("token-new").onclick = () => guard(async () => {
  const result = await post("/api/token/regenerate");
  toast("New token generated, existing clients must pair again");
  $("token").textContent = result.token;
  await refresh();
});

$("token").onclick = () => copy(state.pairingToken, "Token");
$("fingerprint").onclick = () => copy(state.fingerprint, "Fingerprint");

$("add-host").onsubmit = (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  guard(async () => {
    await post("/api/hosts", {
      address: String(form.get("address")).trim(),
      port: Number(form.get("port")) || 57340,
      token: String(form.get("token")).trim(),
      autoConnect: true,
    });
    event.target.reset();
    event.target.querySelector('input[name="port"]').value = "57340";
    toast("Host paired");
    await refresh();
  });
};

$("console-toggle").onclick = () => {
  $("console").classList.toggle("open");
  renderLog();
};

$("theme-toggle").onclick = () => {
  const root = document.documentElement;
  const order = ["auto", "dark", "light"];
  const next = order[(order.indexOf(root.dataset.theme) + 1) % order.length];
  root.dataset.theme = next;
  localStorage.setItem("nksusb-theme", next);
  toast(`Theme: ${next}`);
};

document.documentElement.dataset.theme = localStorage.getItem("nksusb-theme") ?? "auto";

refresh().catch(() => toast("The daemon is not answering", true));
connect();
