// Injected into YouTube's TV app by CastDesk. It:
//   - reports what is playing (and whether it is music), for Windows' media controls, Discord and closing to
//     the tray, and carries out the media controls' buttons;
//   - keeps the page "visible" while a video is open and the window is minimized or closed to the tray, so the
//     TV app keeps playing (home-screen previews still stop);
//   - answers the TV app's format questions as a TV would, so it offers qualities above 720p;
//   - reports how many phones are connected to the TV app (their disconnect never reaches CastDesk over DIAL);
//   - catches F11 and Alt+Enter (WebView2 keeps keys to itself, so CastDesk's window never sees them);
//   - shows Back (top-left) and Fullscreen (top-right) buttons while the mouse moves.
//
//   to CastDesk:   {"type":"media","hasVideo":true,"playing":true,"title":"…","artist":"…","artwork":"https://…",
//                   "hasNext":true,"hasPrevious":false,"watching":true,"music":false,"videoId":"dQw4w9WgXcQ",
//                   "startedAt":1762600000000,"endsAt":1762600213000}   (Unix ms; 0 when paused or unknown)
//                  {"type":"phones","connected":1}       phones connected to the TV app, when that changes
//                  {"type":"fullscreen"}                 toggle fullscreen (key or button)
//                  {"type":"back"}                       the Back button: CastDesk presses Esc
//   from CastDesk: "play" | "pause" | "togglePlay" | "nexttrack" | "previoustrack"
//                  "fullscreen-on" | "fullscreen-off"    the window's current state, for the button's icon
(() => {
  if (window.top !== window || !window.chrome || !window.chrome.webview) return;

  const post = (message) => {
    try { window.chrome.webview.postMessage(message); } catch { /* host gone */ }
  };

  // ---------- Media controls ----------

  // Keep the action handlers the page registers, so Next/Previous do what the page's own buttons do.
  const actionHandlers = {};
  const mediaSession = navigator.mediaSession;
  if (mediaSession && typeof mediaSession.setActionHandler === 'function') {
    const originalSetActionHandler = mediaSession.setActionHandler.bind(mediaSession);
    mediaSession.setActionHandler = (action, handler) => {
      actionHandlers[action] = handler;
      try { originalSetActionHandler(action, handler); } catch { /* unsupported action */ }
    };
  }

  // The TV app's video page (#/watch?v=…); everything else is the home screen, search and so on.
  const isWatching = () => location.hash.startsWith('#/watch');

  // YouTube marks songs, music videos and podcast episodes with a musicVideoType in the player data
  // (MUSIC_VIDEO_TYPE_OMV, _ATV, _UGC, ...); other videos have none. The TV app's player element hands
  // that data out through getPlayerResponse().
  const player = () => document.getElementById('ytlr-player__player-container-player');
  const callPlayer = (name) => {
    const element = player();
    try {
      return element && typeof element[name] === 'function' ? element[name]() : null;
    } catch {
      return null;
    }
  };
  const isMusic = () => {
    const response = callPlayer('getPlayerResponse');
    return !!(response && response.videoDetails && response.videoDetails.musicVideoType);
  };

  // The open video's ID and title, from the player (the media session can lag behind a new video).
  const videoData = () => callPlayer('getVideoData') || {};
  const videoIdFromAddress = () => {
    const match = /[?&]v=([A-Za-z0-9_-]{11})/.exec(location.hash);
    return match ? match[1] : '';
  };

  // When the current playback started and when it will end (Unix ms, 0 if unknown or paused), for
  // Discord's progress bar. Kept steady to within 2 s, so only a seek or a new video changes them.
  let reportedStart = 0;
  const playbackTimes = (video) => {
    if (!video || video.paused || video.ended || !isFinite(video.currentTime)) return { startedAt: 0, endsAt: 0 };
    const rate = video.playbackRate || 1;
    const start = Date.now() - (video.currentTime * 1000) / rate;
    if (Math.abs(start - reportedStart) > 2000) reportedStart = Math.round(start / 1000) * 1000;
    const endsAt = isFinite(video.duration) && video.duration > 0 ? Math.round(reportedStart + (video.duration * 1000) / rate) : 0;
    return { startedAt: reportedStart, endsAt };
  };

  const currentVideo = () => {
    const videos = Array.from(document.querySelectorAll('video'));
    return videos.find((video) => video.currentSrc && !video.ended) || videos[0] || null;
  };

  let lastReport = '';
  const report = () => {
    const video = currentVideo();
    const metadata = mediaSession && mediaSession.metadata;
    const artwork = metadata && metadata.artwork && metadata.artwork.length ? metadata.artwork[metadata.artwork.length - 1].src : '';
    const watching = isWatching();
    const data = watching ? videoData() : {};
    const state = {
      type: 'media',
      hasVideo: !!(video && video.currentSrc),
      playing: !!(video && !video.paused && !video.ended),
      title: (metadata && metadata.title) || data.title || '',
      artist: (metadata && metadata.artist) || data.author || '',
      artwork: artwork || '',
      hasNext: typeof actionHandlers.nexttrack === 'function',
      hasPrevious: typeof actionHandlers.previoustrack === 'function',
      watching,
      music: watching && isMusic(),
      videoId: watching ? (data.video_id || videoIdFromAddress()) : '',
      ...playbackTimes(video),
    };
    const text = JSON.stringify(state);
    if (text === lastReport) return;
    lastReport = text;
    post(state);
  };

  ['play', 'pause', 'playing', 'ended', 'emptied', 'loadedmetadata', 'seeked', 'ratechange'].forEach((name) =>
    document.addEventListener(name, report, true));
  setInterval(report, 1000);

  // ---------- While minimized: videos keep playing, previews stop ----------

  // A minimized window makes the page hidden, and the TV app then unloads its video until the page is
  // visible again (on a TV, the app is only hidden when the user has left it). So while a video is open
  // (the #/watch page), the TV app is told the page is visible and the video keeps playing. Anywhere else,
  // such as the home screen, it is told the truth, so previews stop while CastDesk is minimized. Closing
  // the window still stops everything: CastDesk unloads the page. This script runs before the page's own,
  // so its listener runs first and the TV app only sees the visibility changes passed on here.
  const reallyHidden = Object.getOwnPropertyDescriptor(Document.prototype, 'hidden').get;
  const appearsHidden = () => reallyHidden.call(document) && !isWatching();
  const appearsState = () => (appearsHidden() ? 'hidden' : 'visible');
  Object.defineProperty(document, 'hidden', { configurable: true, get: appearsHidden });
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: appearsState });
  Object.defineProperty(document, 'webkitHidden', { configurable: true, get: appearsHidden });
  Object.defineProperty(document, 'webkitVisibilityState', { configurable: true, get: appearsState });

  // Tell the TV app when what it should see changes: the window is minimized or restored, or a video is
  // opened or closed while minimized (for example, the video ends and the TV app returns to its home screen).
  let toldHidden = appearsHidden();
  let passingOn = false;
  const updateVisibility = () => {
    const hidden = appearsHidden();
    if (hidden === toldHidden) return;
    toldHidden = hidden;
    passingOn = true;
    try { document.dispatchEvent(new Event('visibilitychange', { bubbles: true })); } finally { passingOn = false; }
  };
  ['visibilitychange', 'webkitvisibilitychange'].forEach((name) =>
    window.addEventListener(name, (event) => {
      if (passingOn) return;
      event.stopImmediatePropagation();
      updateVisibility();
    }, true));
  window.addEventListener('hashchange', updateVisibility);
  setInterval(updateVisibility, 1000);

  // ---------- Honest answers about video formats ----------

  // The TV app asks whether formats are supported with extra details (width=3840) and with impossible ones
  // (width=99999) to see if the answers can be trusted. Chromium ignores those details and says yes to
  // both, and the TV app then offers nothing above 720p. A TV says no to the impossible ones, so do the same.
  const browserIsTypeSupported = MediaSource.isTypeSupported.bind(MediaSource);
  const isPossibleDetail = (name, value) => {
    const number = Number(value);
    switch (name) {
      case 'width': return number > 0 && number <= 7680;
      case 'height': return number > 0 && number <= 4320;
      case 'framerate': return number > 0 && number <= 120;
      case 'bitrate': return number > 0 && number <= 500000000;
      case 'channels': return number > 0 && number <= 8;
      case 'eotf':
        if (value === 'bt709') return true;
        return (value === 'smpte2084' || value === 'arib-std-b67') && window.matchMedia('(dynamic-range: high)').matches;
      case 'cryptoblockformat': return value === 'subsample';
      case 'decode-to-texture': return value === 'true' || value === 'false';
      case 'experimental': return value === 'allowed';
      case 'tunnelmode': return value === 'false';
      default: return true;
    }
  };
  MediaSource.isTypeSupported = (type) => {
    if (!browserIsTypeSupported(type)) return false;
    return String(type).split(';').slice(1).every((detail) => {
      const separator = detail.indexOf('=');
      if (separator < 0) return true;
      return isPossibleDetail(detail.slice(0, separator).trim().toLowerCase(), detail.slice(separator + 1).trim().replace(/"/g, ''));
    });
  };


  // ---------- Who is connected ----------

  // "Disconnect from TV" on the phone sends nothing to CastDesk's DIAL server. It reaches the TV app over
  // YouTube's own connection (the "lounge"), a long-running request answered with one message per line:
  //   [[12,["loungeStatus",{"devices":"[{\"type\":\"LOUNGE_SCREEN\",…},{\"type\":\"REMOTE_CONTROL\",…}]",…}]]
  // Each loungeStatus lists everything connected: this TV, and a REMOTE_CONTROL per phone. Counting those
  // means one of two phones leaving doesn't look like the last one leaving. Only the device types are read.
  let phonesConnected = 0;
  const readLoungeMessage = (line) => {
    const match = /^(?:\[|,)(\[\d+,\[.*\])$/.exec(line);
    if (!match || !match[1].includes('"loungeStatus"')) return;
    // A message that ends its chunk carries the chunk's closing bracket too.
    for (const candidate of [match[1], match[1].slice(0, -1)]) {
      try {
        const [, [name, data]] = JSON.parse(candidate);
        if (name !== 'loungeStatus' || !data || typeof data.devices !== 'string') return;
        const phones = JSON.parse(data.devices).filter((device) => device.type === 'REMOTE_CONTROL').length;
        if (phones !== phonesConnected) {
          phonesConnected = phones;
          post({ type: 'phones', connected: phones });
        }
        return;
      } catch { /* try the other form */ }
    }
  };
  const xhrOpen = XMLHttpRequest.prototype.open;
  const xhrSend = XMLHttpRequest.prototype.send;
  const loungeRequests = new WeakSet();
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    if (String(url).includes('/api/lounge/bc/bind')) loungeRequests.add(this);
    return xhrOpen.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function (...args) {
    if (loungeRequests.has(this)) {
      let seen = 0;
      const read = () => {
        try {
          // Up to the last complete line: the rest of a long message may still be on its way.
          const text = this.responseText || '';
          const end = text.lastIndexOf('\n');
          if (end < seen) return;
          text.slice(seen, end).split('\n').forEach(readLoungeMessage);
          seen = end + 1;
        } catch { /* not readable as text */ }
      };
      this.addEventListener('progress', read);
      this.addEventListener('load', read);
    }
    return xhrSend.apply(this, args);
  };

  // ---------- Fullscreen keys ----------

  // Capture phase on window, so this runs before the TV app's own key handling.
  window.addEventListener('keydown', (event) => {
    const isFullscreenKey = event.key === 'F11' || (event.key === 'Enter' && event.altKey);
    if (!isFullscreenKey) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (!event.repeat) post({ type: 'fullscreen' });
  }, true);

  // ---------- Overlay buttons: Back (top-left) and Fullscreen (top-right) ----------

  // Icons, drawn as one SVG path each.
  const ICON_BACK = 'M19 12H5M12 19l-7-7 7-7';
  const ICON_FULLSCREEN = 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5';
  const ICON_EXIT_FULLSCREEN = 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5';
  const HIDE_AFTER_MS = 2500;
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const BACKGROUND = 'rgba(0, 0, 0, 0.6)';
  const BACKGROUND_HOVER = 'rgba(40, 40, 40, 0.9)';

  // The buttons show while the mouse moves and fade out after HIDE_AFTER_MS, unless one is under the pointer.
  const overlayButtons = [];
  let hideTimer = 0;
  let pointerOverAButton = false;

  /**
   * One corner button. YouTube's security policy blocks innerHTML with plain strings (Trusted Types)
   * and inline <style> blocks, so it is built element by element and styled through element.style,
   * which both allow. A shadow root keeps the TV app's own styles off it.
   */
  const createOverlayButton = (side, onClick) => {
    const host = document.createElement('castdesk-controls');
    const shadow = host.attachShadow({ mode: 'closed' });
    Object.assign(host.style, { all: 'initial', position: 'fixed', top: '20px', zIndex: '2147483647', display: 'block' });
    host.style[side] = '20px';

    const button = document.createElement('button');
    button.type = 'button';
    button.tabIndex = -1;
    Object.assign(button.style, {
      display: 'block', boxSizing: 'border-box', width: '48px', height: '48px', padding: '11px', margin: '0',
      border: '0', borderRadius: '10px', background: BACKGROUND, color: '#fff', cursor: 'pointer',
      opacity: '0', pointerEvents: 'none', transition: 'opacity 0.2s ease, background 0.15s ease',
    });

    const icon = document.createElementNS(SVG_NS, 'svg');
    icon.setAttribute('viewBox', '0 0 24 24');
    icon.setAttribute('width', '26');
    icon.setAttribute('height', '26');
    icon.setAttribute('aria-hidden', 'true');
    icon.style.display = 'block';
    const path = document.createElementNS(SVG_NS, 'path');
    for (const [name, value] of Object.entries({
      fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
    })) path.setAttribute(name, value);
    icon.appendChild(path);
    button.appendChild(icon);
    shadow.append(button);

    // Never take focus: the TV app is driven with the arrow keys, and a focused button would steal them.
    button.addEventListener('mousedown', (event) => event.preventDefault());
    button.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      onClick();
      showButtons();
    });
    button.addEventListener('mouseenter', () => { pointerOverAButton = true; button.style.background = BACKGROUND_HOVER; showButtons(); });
    button.addEventListener('mouseleave', () => { pointerOverAButton = false; button.style.background = BACKGROUND; showButtons(); });

    const control = {
      host,
      enabled: true,
      setIcon(pathData, label) {
        path.setAttribute('d', pathData);
        button.title = label;
        button.setAttribute('aria-label', label);
      },
      setVisible(visible) {
        const show = visible && control.enabled;
        button.style.opacity = show ? '1' : '0';
        button.style.pointerEvents = show ? 'auto' : 'none';
      },
    };
    overlayButtons.push(control);
    return control;
  };

  const showButtons = () => {
    // Back on the TV app's home screen would open its exit prompt, so offer it everywhere else only.
    backButton.enabled = !isHomeScreen();
    overlayButtons.forEach((control) => control.setVisible(true));
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      if (!pointerOverAButton) overlayButtons.forEach((control) => control.setVisible(false));
    }, HIDE_AFTER_MS);
  };

  const isHomeScreen = () => location.hash === '' || location.hash === '#' || location.hash === '#/' || location.hash.startsWith('#/?');

  // Back asks CastDesk to press Esc (the TV app's Back) as a real key, which the TV app accepts like a remote's button.
  const backButton = createOverlayButton('left', () => post({ type: 'back' }));
  backButton.setIcon(ICON_BACK, 'Back (Esc)');

  const fullscreenButton = createOverlayButton('right', () => post({ type: 'fullscreen' }));
  const setFullscreenIcon = (isFullscreen) => fullscreenButton.setIcon(
    isFullscreen ? ICON_EXIT_FULLSCREEN : ICON_FULLSCREEN,
    isFullscreen ? 'Exit fullscreen (F11)' : 'Fullscreen (F11)');
  setFullscreenIcon(false);

  window.addEventListener('mousemove', showButtons, true);

  // The TV app rebuilds large parts of the page; put the buttons back whenever they are removed.
  const attach = () => {
    if (!document.documentElement) return;
    overlayButtons.forEach(({ host }) => { if (!host.isConnected) document.documentElement.appendChild(host); });
  };
  attach();
  document.addEventListener('DOMContentLoaded', attach);
  setInterval(attach, 1000);

  // ---------- Commands from CastDesk ----------

  window.chrome.webview.addEventListener('message', (event) => {
    const command = event.data;
    if (command === 'fullscreen-on' || command === 'fullscreen-off') {
      setFullscreenIcon(command === 'fullscreen-on');
      return;
    }
    if ((command === 'nexttrack' || command === 'previoustrack') && typeof actionHandlers[command] === 'function') {
      try { actionHandlers[command]({ action: command }); } catch { /* page error */ }
      return;
    }
    const video = currentVideo();
    if (!video) return;
    if (command === 'play' || (command === 'togglePlay' && video.paused)) video.play();
    else if (command === 'pause' || command === 'togglePlay') video.pause();
    setTimeout(report, 200);
  });
})();
