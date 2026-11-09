// Checks Tv/page-bridge.js against the live TV app, without the CastDesk window, sound or the user's sign-in.
//
// Opens youtube.com/tv in a headless, muted Edge with its own temporary profile, presents CastDesk's TV identity
// (DefaultUserAgent, read from Configuration/CastDeskOptions.cs), stands in for WebView2 (chrome.webview.postMessage
// into an array) and loads the bridge. Then prints what the TV app sends on YouTube's connection (the lounge
// /api/lounge/bc/bind: name and deviceInfo, and YouTube's status) and the messages the bridge posted to CastDesk.
//
//   node tools/check-bridge.js [--name CastDesk-TEST] [--video dQw4w9WgXcQ] [--no-bridge] [--seconds 25] [--port 9345]
//
//   --name       the name CastDesk would give the TV app (window.__castDeskScreenName)
//   --video      open #/watch?v=<id> instead of the home screen
//   --no-bridge  leave the bridge out, to see what the TV app sends on its own ("YouTube on TV", model "Sabrina")
//
// With the bridge, bind should show name=<--name> and deviceInfo.model=<--name>, with status 200.
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : fallback;
};
const screenName = option('--name', 'CastDesk-TEST');
const video = option('--video', '');
const seconds = Number(option('--seconds', '25'));
const port = Number(option('--port', '9345'));
const withBridge = !args.includes('--no-bridge');

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
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'castdesk-check-'));
const browser = spawn(edge, ['--headless=new', '--mute-audio', `--remote-debugging-port=${port}`,
  '--remote-debugging-address=127.0.0.1', `--user-data-dir=${profile}`, '--no-first-run', 'about:blank'], { stdio: 'ignore' });

const browserExited = new Promise((resolve) => browser.once('exit', resolve));
let closeBrowser = () => browser.kill();
const cleanUp = async () => {
  closeBrowser();
  // Edge's child processes keep the profile open for a moment after the main one is asked to close.
  await Promise.race([browserExited, sleep(5000)]);
  if (browser.exitCode === null) browser.kill();
  for (let attempt = 0; attempt < 40; attempt++) {
    try { fs.rmSync(profile, { recursive: true, force: true }); return; } catch { await sleep(250); }
  }
  console.error(`Could not delete the temporary profile ${profile}`);
};

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
  const binds = new Set();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
      pending.get(message.id)(message.result);
      pending.delete(message.id);
    } else if (message.method === 'Network.requestWillBeSent' && message.params.request.url.includes('/api/lounge/bc/bind')) {
      binds.add(message.params.requestId);
      const query = new URL(message.params.request.url).searchParams;
      const info = query.get('deviceInfo');
      console.log(`bind: name=${query.get('name')}${info ? ` deviceInfo=${info}` : ''}`);
    } else if (message.method === 'Network.responseReceived' && binds.has(message.params.requestId)) {
      console.log(`  status ${message.params.response.status}`);
    }
  };
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  // Browser.close lets Edge shut down its child processes too, so the profile can be deleted.
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
  if (withBridge) {
    const stub = `window.__castDeskScreenName = ${JSON.stringify(screenName)}; window.__castDeskMessages = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = { postMessage: (m) => window.__castDeskMessages.push(m), addEventListener() {}, removeEventListener() {} };`;
    await send('Page.addScriptToEvaluateOnNewDocument', { source: `${stub}\n${bridge}` });
  }
  const url = video ? `https://www.youtube.com/tv#/watch?v=${encodeURIComponent(video)}` : 'https://www.youtube.com/tv';
  console.log(`Opening ${url} ${withBridge ? `with the bridge, name "${screenName}"` : 'without the bridge'}; waiting ${seconds} s.`);
  await send('Page.navigate', { url });
  await sleep(seconds * 1000);

  if (withBridge) {
    const result = await send('Runtime.evaluate', { expression: 'window.__castDeskMessages || []', returnByValue: true });
    const messages = result?.result?.value || [];
    console.log(`Bridge messages to CastDesk (${messages.length}):`);
    for (const message of messages) console.log(`  ${JSON.stringify(message)}`);
  }
})()
  .catch((error) => { console.error(`Failed: ${error.message}`); process.exitCode = 1; })
  .finally(cleanUp);
