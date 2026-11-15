/*
 * Humlog slideshow. Runs on an old webOS TV browser, so: plain ES5 only
 * (no let/const, arrow functions, fetch, async/await or optional chaining).
 * Reads catalogue.json (written by the Sync function, most recently added first) and plays what was added in the
 * last NEW_DAYS first, then the rest shuffled. Up/Red goes back to that order; Down/Green shuffles everything.
 */
(function () {
  'use strict';

  var params = parseParams(location.search);
  var PHOTO_MS = number(params.secs, 8) * 1000;
  var FX = params.fx || 'fade';                  // fade | slide | none (fade was smoothest on the TV)
  var NEW_DAYS = number(params.newDays, 7);      // added to the album this recently = played first, newest first
  var PHOTOS_ONLY = params.photosOnly === '1';
  var MAX_VIDEO_MS = 3 * 60 * 1000;              // longer ones take too long to buffer on the TV
  var START_TIMEOUT_MS = 15000;                  // skip an item that hasn't loaded/started by then
  var STALL_TIMEOUT_MS = 30000;                  // skip a video stuck buffering mid-way
  var REFRESH_MS = 10 * 60 * 1000;
  var RETRY_MS = 30000;
  var TRANSITION_MS = 600;

  var stage = byId('stage');
  var pausedBadge = byId('paused');
  var hint = byId('hint');
  var modeBadge = byId('mode');
  var msg = byId('msg');

  var items = [];            // play order
  var latest = [];           // last catalogue as loaded: most recently added first
  var known = {};
  var index = -1;
  var current = null;        // { item, el, media, started } on screen
  var pending = null;        // photo slide still loading, not yet on screen
  var paused = false;
  var soundBlocked = false;  // browser refused autoplay with sound; cleared once the viewer unmutes
  var advanceTimer = null;
  var watchdog = null;
  var cursorTimer = null;
  var flashTimer = null;
  var lastKeyAt = 0;

  // ---- catalogue ----------------------------------------------------------

  function loadCatalogue(done) {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', 'catalogue.json?t=' + new Date().getTime() + (window.KEY_QUERY ? '&' + window.KEY_QUERY : ''), true);
    xhr.timeout = 30000;
    xhr.onload = function () {
      var list = null;
      if (xhr.status === 200) {
        try { list = JSON.parse(xhr.responseText).items; } catch (e) { list = null; }
      }
      done(list && list.length ? filter(list) : null);
    };
    xhr.onerror = xhr.ontimeout = function () { done(null); };
    xhr.send();
  }

  function filter(list) {
    var out = [];
    for (var i = 0; i < list.length; i++) {
      var it = list[i];
      var video = it.type === 'video' && !PHOTOS_ONLY && (it.durationMs || 0) <= MAX_VIDEO_MS;
      if (it.type === 'photo' || video) out.push(it);
    }
    return out;
  }

  function start() {
    loadCatalogue(function (list) {
      if (!list) {
        showMessage('Could not load photos. Trying again…');
        setTimeout(start, RETRY_MS);
        return;
      }
      showMessage('');
      latest = list;
      items = newFirst(list);
      markKnown(items);
      go(0, 0);
      setInterval(refresh, REFRESH_MS);
    });
  }

  // Keeps the current position; anything new in the album is played next.
  function refresh() {
    loadCatalogue(function (list) {
      if (!list) return;
      latest = list;

      var fresh = {};
      var newcomers = [];
      for (var i = 0; i < list.length; i++) {
        fresh[list[i].id] = list[i];
        if (!known[list[i].id]) newcomers.push(list[i]);
      }

      var kept = [];
      var pos = -1;
      for (var j = 0; j < items.length; j++) {
        if (fresh[items[j].id]) kept.push(fresh[items[j].id]);
        if (j === index) pos = kept.length - 1;
      }

      items = kept.slice(0, pos + 1).concat(newcomers, kept.slice(pos + 1));
      index = pos;
      markKnown(newcomers);
    });
  }

  function markKnown(list) {
    for (var i = 0; i < list.length; i++) known[list[i].id] = true;
  }

  // ---- showing items ------------------------------------------------------

  function next() { go(index + 1, 1); }

  // Up/Red: back to the recent additions, then the rest shuffled.
  function restartFromNewest() {
    if (!latest.length) return;
    items = newFirst(latest);
    go(0, -1);
    flash('Newest first');
  }

  // Down/Green: skip the recent additions and shuffle everything.
  function shuffleAll() {
    if (!latest.length) return;
    items = shuffle(latest);
    go(0, 1);
    flash('Shuffled');
  }

  // The catalogue is most recently added first, so the new ones are a prefix; an unparseable date counts as old.
  function newFirst(list) {
    var cutoff = new Date().getTime() - NEW_DAYS * 24 * 60 * 60 * 1000;
    var n = 0;
    while (n < list.length && Date.parse(list[n].addedUtc) >= cutoff) n++;
    return list.slice(0, n).concat(shuffle(list.slice(n)));
  }
  function prev() { go(index - 1, -1); }

  function go(i, dir) {
    if (!items.length) return;
    index = ((i % items.length) + items.length) % items.length;
    show(items[index], dir);
    preload(items[(index + 1) % items.length]);
    preload(items[(index - 1 + items.length) % items.length]);
  }

  // A photo only starts moving once it has loaded and been painted off-screen: decoding a full-screen
  // JPEG during the slide makes TV graphics stutter. Until then the previous item stays on screen.
  function show(item, dir) {
    clearTimers();
    hide(hint);
    if (pending) {
      release(pending);
      pending = null;
    }

    var el = document.createElement('div');
    el.className = 'slide instant';
    var fill = document.createElement('div');
    fill.className = 'fill';
    // A tiny thumbnail stretched to full screen looks blurred for free (a CSS blur is too slow on the TV).
    fill.style.backgroundImage = 'url("' + sized(item.src, 32, 32) + '")';
    el.appendChild(fill);

    var media;
    if (item.type === 'video') {
      media = document.createElement('video');
      media.poster = sized(item.src);
      media.preload = 'auto';
      media.setAttribute('playsinline', '');
      // Google's transcode (upright, seekable) as chosen by the Sync function; the original until it has probed.
      media.src = item.src + (item.variant || '=dv');
      media.addEventListener('playing', onVideoPlaying);
      media.addEventListener('waiting', onVideoWaiting);
      media.addEventListener('ended', onVideoDone);
      media.addEventListener('error', onVideoError);
    } else {
      media = document.createElement('img');
    }
    media.className = 'media';
    el.appendChild(media);

    var slide = { item: item, el: el, media: media, started: false };
    watchdog = setTimeout(skipIfNotStarted, START_TIMEOUT_MS);

    if (item.type === 'video') {
      reveal(slide, dir);
      if (!paused) playVideo(media);
      return;
    }

    pending = slide;
    media.onload = function () {
      if (pending === slide) parkThenReveal(slide, dir);
    };
    media.onerror = function () {
      if (pending !== slide) return;
      pending = null;
      release(slide);
      go(index + (dir < 0 ? -1 : 1), dir || 1);
    };
    media.src = sized(item.src);
  }

  // Put the loaded slide just off-screen, let the browser paint it, then slide it in.
  function parkThenReveal(slide, dir) {
    if (FX !== 'slide' || !dir || !current) {
      reveal(slide, dir);
      return;
    }
    place(slide.el, dir);
    stage.appendChild(slide.el);
    afterPaint(function () {
      if (pending === slide) reveal(slide, dir);
    });
  }

  function reveal(slide, dir) {
    pending = null;
    var old = current;
    current = slide;
    if (slide.item.type === 'photo') {
      slide.started = true;
      clearTimeout(watchdog);
      if (!paused) scheduleAdvance(PHOTO_MS);
    }
    enter(slide.el, old ? dir : 0);
    if (old) leave(old, dir);
  }

  function enter(el, dir) {
    if (FX === 'none' || !dir) {
      el.className = 'slide instant';
      place(el, 0);
      if (!el.parentNode) stage.appendChild(el);
      return;
    }
    if (!el.parentNode) {
      place(el, dir);
      stage.appendChild(el);
    }
    void el.offsetWidth; // force layout so the transition runs
    el.className = 'slide';
    place(el, 0);
  }

  function afterPaint(fn) {
    if (!window.requestAnimationFrame) {
      setTimeout(fn, 50);
      return;
    }
    window.requestAnimationFrame(function () { window.requestAnimationFrame(fn); });
  }

  function leave(old, dir) {
    if (FX === 'none') { release(old); return; }
    // Fade: the new slide (appended later, so on top) fades in over the old one, which just waits underneath.
    if (FX !== 'fade') place(old.el, -dir);
    setTimeout(function () { release(old); }, TRANSITION_MS + 100);
  }

  function place(el, pos) {
    var t = FX === 'slide' ? 'translate3d(' + (pos * 100) + '%,0,0)' : 'none';
    el.style.webkitTransform = t;
    el.style.transform = t;
    el.style.opacity = FX === 'fade' && pos !== 0 ? '0' : '1';
  }

  // Old TV browsers keep video buffers alive unless the source is explicitly dropped.
  function release(slide) {
    var m = slide.media;
    if (m.tagName === 'VIDEO') {
      m.pause();
      m.removeAttribute('src');
      try { m.load(); } catch (e) { /* ignore */ }
    } else {
      m.onload = m.onerror = null;
    }
    if (slide.el.parentNode) slide.el.parentNode.removeChild(slide.el);
  }

  function preload(item) {
    if (item && item.type === 'photo') new Image().src = sized(item.src);
  }

  // ---- photo timing -------------------------------------------------------

  function scheduleAdvance(ms) {
    clearTimeout(advanceTimer);
    advanceTimer = setTimeout(next, ms);
  }

  // ---- video --------------------------------------------------------------

  function playVideo(v) {
    v.muted = soundBlocked;
    showSoundHint(v);
    var p;
    try { p = v.play(); } catch (e) { p = null; }
    if (p && p.then) {
      p.then(null, function (err) {
        if (!isCurrent(v) || v.muted || !err || err.name !== 'NotAllowedError') return;
        soundBlocked = true;   // autoplay with sound refused: play muted and offer OK for sound
        v.muted = true;
        showSoundHint(v);
        v.play();
      });
    }
  }

  function onVideoPlaying() {
    if (!isCurrent(this)) return;
    current.started = true;
    clearTimeout(watchdog);
  }

  function onVideoWaiting() {
    if (!isCurrent(this) || !current.started) return;
    clearTimeout(watchdog);
    watchdog = setTimeout(function () { if (current && current.media.readyState < 3) next(); }, STALL_TIMEOUT_MS);
  }

  function onVideoDone() {
    if (isCurrent(this)) next();
  }

  // If the transcode won't load here, try the original once before skipping the video.
  function onVideoError() {
    var v = this;
    if (!isCurrent(v)) return;
    var original = current.item.src + '=dv';
    if (v.src === original) { next(); return; }
    v.src = original;
    clearTimeout(watchdog);
    watchdog = setTimeout(skipIfNotStarted, START_TIMEOUT_MS);
    if (!paused) playVideo(v);
  }

  function unmute() {
    if (!current || current.media.tagName !== 'VIDEO' || !current.media.muted) return false;
    soundBlocked = false;
    current.media.muted = false;
    hide(hint);
    return true;
  }

  function showSoundHint(v) {
    if (v.muted) unhide(hint); else hide(hint);
  }

  function skipIfNotStarted() {
    if (paused) return;
    if (pending) {
      release(pending);
      pending = null;
      next();
    } else if (current && !current.started) {
      next();
    }
  }

  // ---- pause / input ------------------------------------------------------

  function setPaused(p) {
    paused = p;
    if (p) unhide(pausedBadge); else hide(pausedBadge);
    if (!current) return;

    clearTimeout(advanceTimer);
    if (current.item.type === 'video') {
      if (p) current.media.pause(); else playVideo(current.media);
    } else if (!p && current.started) {
      scheduleAdvance(PHOTO_MS);
    }
    if (!p && !current.started) {
      clearTimeout(watchdog);
      watchdog = setTimeout(skipIfNotStarted, START_TIMEOUT_MS);
    }
  }

  function ok() {
    if (!unmute()) setPaused(!paused);
  }

  document.addEventListener('keydown', function (e) {
    lastKeyAt = new Date().getTime();
    switch (e.keyCode) {
      case 39: next(); flash('Next'); break;        // Right
      case 37: prev(); flash('Previous'); break;    // Left
      case 38: case 403: restartFromNewest(); break;  // Up, LG red button: start again from the newest
      case 40: case 404: shuffleAll(); break;         // Down, LG green button: shuffle everything
      case 13: case 32: case 179: ok(); break;  // OK / Enter, Space, media Play-Pause
      case 415: if (!unmute()) setPaused(false); break;  // LG Play
      case 19: setPaused(true); break;          // LG Pause
      case 461: case 8: break;                  // LG Back / Backspace: stay on the slideshow
      default: return;
    }
    e.preventDefault();
  });

  // Magic Remote pointer or mouse: left third = previous, right third = next, middle = OK.
  // With the pointer showing, one OK press can arrive as a key and a click; acting on both would undo the first.
  document.addEventListener('click', function (e) {
    if (new Date().getTime() - lastKeyAt < 500) return;
    var x = e.clientX / window.innerWidth;
    if (x < 1 / 3) { prev(); flash('Previous'); } else if (x > 2 / 3) { next(); flash('Next'); } else ok();
  });

  document.addEventListener('mousemove', function () {
    document.body.style.cursor = 'default';
    clearTimeout(cursorTimer);
    cursorTimer = setTimeout(function () { document.body.style.cursor = 'none'; }, 3000);
  });

  // ---- helpers ------------------------------------------------------------

  // Google resizes on the fly: base URL + "=w{W}-h{H}" fits the picture inside that box.
  function sized(src, w, h) {
    var ratio = window.devicePixelRatio || 1;
    w = w || Math.min(1920, Math.round(window.innerWidth * ratio));
    h = h || Math.min(1080, Math.round(window.innerHeight * ratio));
    return src + '=w' + w + '-h' + h;
  }

  function isCurrent(media) {
    return current !== null && current.media === media;
  }

  function clearTimers() {
    clearTimeout(advanceTimer);
    clearTimeout(watchdog);
  }

  function shuffle(list) {
    var a = list.slice();
    for (var i = a.length - 1; i > 0; i--) {
      var j = Math.floor(Math.random() * (i + 1));
      var t = a[i]; a[i] = a[j]; a[j] = t;
    }
    return a;
  }

  // A short-lived badge confirming a remote button press straight away, before the next photo has loaded.
  function flash(text) {
    modeBadge.textContent = text;
    unhide(modeBadge);
    clearTimeout(flashTimer);
    flashTimer = setTimeout(function () { hide(modeBadge); }, 2000);
  }

  function showMessage(text) {
    msg.textContent = text;
    if (text) unhide(msg); else hide(msg);
  }

  function parseParams(search) {
    var out = {};
    var pairs = search.replace(/^\?/, '').split('&');
    for (var i = 0; i < pairs.length; i++) {
      var kv = pairs[i].split('=');
      if (kv[0]) out[decodeURIComponent(kv[0])] = decodeURIComponent(kv[1] || '');
    }
    return out;
  }

  function number(value, fallback) {
    var n = parseFloat(value);
    return n > 0 ? n : fallback;
  }

  function byId(id) { return document.getElementById(id); }
  function hide(el) { if (el.className.indexOf('hidden') < 0) el.className += ' hidden'; }
  function unhide(el) { el.className = el.className.replace(/\s*hidden/g, ''); }

  // Opening a bookmark can leave focus on the TV browser's own toolbar, so remote keys never reach the page
  // until something (e.g. Pause twice) moves it. Take focus on load, and again whenever the page gets it back.
  function takeFocus() {
    try { window.focus(); document.body.focus(); } catch (e) { /* ignore */ }
  }
  document.body.setAttribute('tabindex', '-1');
  takeFocus();
  window.addEventListener('load', takeFocus);
  window.addEventListener('pageshow', takeFocus);
  window.addEventListener('focus', takeFocus);

  start();
})();
