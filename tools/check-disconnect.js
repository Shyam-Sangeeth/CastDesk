// Checks what happens when a phone disconnects, without a phone, sound or the CastDesk window.
//
// Opens youtube.com/tv in a headless, muted Edge with its own temporary profile (signed out, so videos may start
// with an ad), with CastDesk's TV identity and Tv/page-bridge.js as in check-bridge.js. Then an emulated phone
// joins the TV app over YouTube's lounge as a REMOTE_CONTROL (the way the YouTube app does once it reconnects
// through its saved link: pairingType "manual"), plays a video, and disconnects. The TV app's address, the video's
// position and the bridge's messages are printed as they change.
//
//   node tools/check-disconnect.js [--video pf5tXJZM1Rw] [--play 15] [--after 25] [--no-keep-playing]
//                                  [--pairing manual|dial] [--port 9347]
//
//   --play             seconds the phone lets the video play before disconnecting
//   --after            seconds to keep watching after the disconnect
//   --no-keep-playing  "Keep playing when my phone disconnects" off (window.__castDeskKeepPlaying = false)
//
// Recorded 2026-11-10: when the phone leaves, the TV app goes back to its home screen (#/). With Keep playing on,
// the bridge reports "reopening" and loads #/watch?t=<seconds>s&v=<video>, which plays on from there.
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : fallback;
};
const video = option('--video', 'pf5tXJZM1Rw');
const playSeconds = Number(option('--play', '15'));
const afterSeconds = Number(option('--after', '25'));
const pairing = option('--pairing', 'manual');
const port = Number(option('--port', '9347'));
const keepPlaying = !args.includes('--no-keep-playing');

const repo = path.resolve(__dirname, '..');
const optionsSource = fs.readFileSync(path.join(repo, 'Configuration', 'CastDeskOptions.cs'), 'utf8');
const userAgentMatch = /DefaultUserAgent\s*=\s*((?:\s*"(?:[^"\\]|\\.)*"\s*\+?)+);/.exec(optionsSource);
if (!userAgentMatch) throw new Error('DefaultUserAgent not found in CastDeskOptions.cs');
const userAgent = [...userAgentMatch[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((part) => JSON.parse(`"${part[1]}"`)).join('');
const bridge = fs.readFileSync(path.join(repo, 'Tv', 'page-bridge.js'), 'utf8');

const edgeCandidates = [
  path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
];
const edge = edgeCandidates.find((candidate) => fs.existsSync(candidate));
if (!edge) throw new Error('Microsoft Edge not found');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const started = Date.now();
const log = (...parts) => console.log(`${((Date.now() - started) / 1000).toFixed(1).padStart(5)} s `, ...parts);

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'castdesk-check-'));
// Autoplay without a click, as CastDesk's WebView2 allows; otherwise headless Edge never starts the video.
const browser = spawn(edge, ['--headless=new', '--mute-audio', '--autoplay-policy=no-user-gesture-required',
  `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1', `--user-data-dir=${profile}`,
  '--no-first-run', 'about:blank'], { stdio: 'ignore' });
const browserExited = new Promise((resolve) => browser.once('exit', resolve));
let closeBrowser = () => browser.kill();
const cleanUp = async () => {
  closeBrowser();
  await Promise.race([browserExited, sleep(5000)]);
  if (browser.exitCode === null) browser.kill();
  for (let attempt = 0; attempt < 40; attempt++) {
    try { fs.rmSync(profile, { recursive: true, force: true }); return; } catch { await sleep(250); }
  }
  console.error(`Could not delete the temporary profile ${profile}`);
};

// ---------- The emulated phone (YouTube's lounge protocol, as the YouTube app on Android uses it) ----------

const lounge = 'https://www.youtube.com/api/lounge';
const form = (fields) => new URLSearchParams(Object.entries(fields).map(([key, value]) => [key, String(value)]));

class Phone {
  constructor(screenId) {
    this.screenId = screenId;
    this.id = crypto.randomUUID();
    this.rid = 1;
  }

  async connect() {
    const tokens = await (await fetch(`${lounge}/pairing/get_lounge_token_batch`, {
      method: 'POST', body: form({ screen_ids: this.screenId }),
    })).json();
    this.token = tokens.screens[0].loungeToken;
    const response = await fetch(`${lounge}/bc/bind?RID=1&VER=8&CVER=1&auth_failure_option=send_error`, {
      method: 'POST',
      body: form({
        app: 'android-phone-21.44.534', 'mdx-version': 3, name: 'CastDesk test phone', id: this.id,
        device: 'REMOTE_CONTROL', capabilities: 'que,vsp,pas,asw,dsdtr,mvp,ctops,apw,atp,ndt,mus',
        clientName: 'android', pairingType: pairing, ui: 'true', theme: 'cl', loungeIdToken: this.token,
      }),
    });
    const text = await response.text();
    this.sid = /\["c","([^"]+)"/.exec(text)?.[1];
    this.gsession = /\["S","([^"]+)"/.exec(text)?.[1];
    if (!response.ok || !this.sid || !this.gsession) throw new Error(`Phone couldn't connect: ${response.status}`);
    // Stay connected the way a phone does: a long request that the lounge answers with events.
    this.listening = new AbortController();
    fetch(`${lounge}/bc/bind?${this.params({ RID: 'rpc', CI: 0, TYPE: 'xmlhttp' })}`, { signal: this.listening.signal })
      .then((reply) => reply.text()).catch(() => {});
  }

  params(extra) {
    return form({
      name: 'CastDesk test phone', loungeIdToken: this.token, SID: this.sid, gsessionid: this.gsession,
      device: 'REMOTE_CONTROL', app: 'android-phone-21.44.534', VER: 8, v: 2, ...extra,
    });
  }

  async command(name, fields = {}) {
    const body = { count: 1, ofs: this.rid, req0__sc: name };
    for (const [key, value] of Object.entries(fields)) body[`req0_${key}`] = value;
    this.rid++;
    const response = await fetch(`${lounge}/bc/bind?${this.params({ RID: this.rid })}`, { method: 'POST', body: form(body) });
    return response.ok;
  }

  async disconnect() {
    const response = await fetch(`${lounge}/bc/bind?${this.params({ CVER: 1, RID: this.rid, auth_failure_option: 'send_error' })}`, {
      method: 'POST',
      body: form({ ui: '', TYPE: 'terminate', clientDisconnectReason: 'MDX_SESSION_DISCONNECT_REASON_DISCONNECTED_BY_USER' }),
    });
    this.listening?.abort();
    return response.ok;
  }
}

// ---------- The test TV ----------

(async () => {
  let targets;
  for (let attempt = 0; attempt < 40 && !targets; attempt++) {
    try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(250); }
  }
  if (!targets) throw new Error(`Edge didn't open its debugging port ${port}`);
  const socket = new WebSocket(targets.find((target) => target.type === 'page').webSocketDebuggerUrl);
  let nextId = 0;
  const pending = new Map();
  const send = (method, params = {}) => new Promise((resolve) => {
    const id = ++nextId;
    pending.set(id, resolve);
    socket.send(JSON.stringify({ id, method, params }));
  });
  let screenId = null;
  const screenRequests = new Set();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
      pending.get(message.id)(message.result);
      pending.delete(message.id);
    } else if (message.method === 'Runtime.consoleAPICalled') {
      const text = message.params.args?.[0]?.value;
      if (typeof text === 'string' && text.startsWith('castdesk-bridge ')) log('bridge', text.slice(16));
    } else if (message.method === 'Network.requestWillBeSent' && message.params.request.url.includes('/pairing/generate_screen_id')) {
      screenRequests.add(message.params.requestId);
    } else if (message.method === 'Network.loadingFinished' && screenRequests.has(message.params.requestId)) {
      // The TV app asks YouTube for its screen ID, which a phone needs to join it.
      send('Network.getResponseBody', { requestId: message.params.requestId }).then((result) => {
        screenId = /"screenId"\s*:\s*"([^"]+)"/.exec(result?.body || '')?.[1] || screenId;
      });
    }
  };
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  closeBrowser = () => {
    try {
      const control = new WebSocket(version.webSocketDebuggerUrl);
      control.onopen = () => control.send(JSON.stringify({ id: 1, method: 'Browser.close' }));
      control.onerror = () => browser.kill();
    } catch { browser.kill(); }
  };

  await send('Network.enable');
  await send('Network.setUserAgentOverride', { userAgent });
  await send('Page.enable');
  // The bridge's messages go out through the console, so they are seen even when the page reloads.
  await send('Runtime.enable');
  const stub = `window.__castDeskScreenName = 'CastDesk-TEST'; window.__castDeskKeepPlaying = ${keepPlaying};
    window.chrome = window.chrome || {};
    window.chrome.webview = { postMessage: (m) => { if (m.type !== 'media') console.debug('castdesk-bridge ' + JSON.stringify(m)); },
      addEventListener() {}, removeEventListener() {} };`;
  await send('Page.addScriptToEvaluateOnNewDocument', { source: `${stub}\n${bridge}` });
  log(`Opening the test TV (Keep playing ${keepPlaying ? 'on' : 'off'}).`);
  await send('Page.navigate', { url: 'https://www.youtube.com/tv' });

  // Print the address and the video's position (every 5 s of video) as they change.
  const probe = `JSON.stringify((() => {
    const video = [...document.querySelectorAll('video')].find((v) => v.currentSrc) || document.querySelector('video');
    return { address: location.hash, video: video ? { playing: !video.paused, seconds: Math.round(video.currentTime) } : null };
  })())`;
  let shown = '';
  let watching = true;
  const watch = (async () => {
    while (watching) {
      const result = await send('Runtime.evaluate', { expression: probe, returnByValue: true });
      const state = result?.result?.value ? JSON.parse(result.result.value) : null;
      if (state) {
        const key = JSON.stringify([state.address, state.video?.playing, state.video ? Math.floor(state.video.seconds / 5) : -1]);
        if (key !== shown) {
          shown = key;
          log(state.address || '(no address)', state.video ? `${state.video.playing ? 'playing' : 'paused'} at ${state.video.seconds} s` : 'no video');
        }
      }
      await sleep(250);
    }
  })();

  for (let attempt = 0; attempt < 80 && !screenId; attempt++) await sleep(250);
  if (!screenId) throw new Error("The TV app didn't get a screen ID");
  const phone = new Phone(screenId);
  await phone.connect();
  log(`Phone connected (pairing "${pairing}").`);
  await sleep(2000);
  log(`Phone plays ${video}: ${await phone.command('setPlaylist', { videoId: video })}`);
  await sleep(playSeconds * 1000);
  log(`Phone disconnects: ${await phone.disconnect()}`);
  await sleep(afterSeconds * 1000);
  watching = false;
  await watch;
})()
  .catch((error) => { console.error(`Failed: ${error.message}`); process.exitCode = 1; })
  .finally(cleanUp);
