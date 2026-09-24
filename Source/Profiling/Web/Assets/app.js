/* DPA Web Monitor - dashboard logic. No external dependencies. */
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  var state = {
    payload: null,
    connected: false,
    logSort: 'percent',
    logFilter: '',
    spikeSort: null,
    spikeSortDesc: true,
    lastMessageAt: 0
  };

  // ---------------------------------------------------------------- helpers

  function num(v, digits) {
    if (v === null || v === undefined || !Number.isFinite(v)) return '—';
    return Number(v).toFixed(digits === undefined ? 1 : digits);
  }

  function int(v) {
    if (v === null || v === undefined || !Number.isFinite(v)) return '—';
    return Math.round(v).toString();
  }

  function clock(seconds) {
    if (!Number.isFinite(seconds)) return '—';
    var s = Math.max(0, Math.floor(seconds));
    var m = Math.floor(s / 60);
    var sec = s % 60;
    if (m >= 60) {
      var h = Math.floor(m / 60);
      return h + ':' + String(m % 60).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
    }
    return String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
  }

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function severityClass(ms, threshold) {
    if (ms >= threshold * 3) return 'hot';
    if (ms >= threshold) return 'warm';
    return '';
  }

  // ---------------------------------------------------------------- canvas

  function prepare(canvas, cssHeight) {
    var dpr = window.devicePixelRatio || 1;
    var cssW = canvas.clientWidth || canvas.parentNode.clientWidth || 600;
    var cssH = cssHeight;

    if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
      canvas.width = Math.round(cssW * dpr);
      canvas.height = Math.round(cssH * dpr);
      canvas.style.height = cssH + 'px';
    }

    var ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);
    return { ctx: ctx, w: cssW, h: cssH };
  }

  function niceMax(value) {
    if (!Number.isFinite(value) || value <= 0) return 1;
    var exp = Math.pow(10, Math.floor(Math.log10(value)));
    var frac = value / exp;
    var step = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
    return step * exp;
  }

  /**
   * draws one chart. config:
   *   series: [{ data: [], color, fill, width, dashed }]
   *   height, unit, threshold (y value), thresholdColor
   */
  function drawChart(canvas, config) {
    var surface = prepare(canvas, config.height || 150);
    var ctx = surface.ctx, W = surface.w, H = surface.h;

    var padL = 46, padR = 10, padT = 16, padB = 16;
    var plotW = Math.max(10, W - padL - padR);
    var plotH = Math.max(10, H - padT - padB);

    var maxLen = 0;
    for (var i = 0; i < config.series.length; i++) {
      maxLen = Math.max(maxLen, config.series[i].data ? config.series[i].data.length : 0);
    }

    if (maxLen < 2) {
      ctx.fillStyle = '#7d8b9b';
      ctx.font = '12px Consolas, monospace';
      ctx.fillText('等待数据…', padL, padT + plotH / 2);
      return;
    }

    var yMax = config.yMax || 0;
    var yMin = config.yMin || 0;
    if (!config.yMax) {
      for (var s = 0; s < config.series.length; s++) {
        var d = config.series[s].data || [];
        for (var k = 0; k < d.length; k++) {
          if (Number.isFinite(d[k]) && d[k] > yMax) yMax = d[k];
        }
      }
      if (config.threshold && config.threshold > yMax) yMax = config.threshold;
      yMax = niceMax(yMax * 1.15);
    }

    function xAt(index) { return padL + (maxLen <= 1 ? 0 : (index / (maxLen - 1)) * plotW); }
    function yAt(value) {
      var clamped = Math.max(yMin, Math.min(yMax, value));
      return padT + plotH - ((clamped - yMin) / (yMax - yMin)) * plotH;
    }

    // grid + y labels
    ctx.strokeStyle = '#232d3a';
    ctx.fillStyle = '#66737f';
    ctx.font = '10px Consolas, monospace';
    ctx.lineWidth = 1;
    var rows = 4;
    for (var r = 0; r <= rows; r++) {
      var y = padT + (plotH / rows) * r;
      ctx.beginPath();
      ctx.moveTo(padL, y + 0.5);
      ctx.lineTo(padL + plotW, y + 0.5);
      ctx.stroke();

      var label = yMax - ((yMax - yMin) / rows) * r;
      ctx.fillText(num(label, label >= 100 ? 0 : 1), 4, y + 3);
    }

    // threshold marker
    if (config.threshold && config.threshold <= yMax) {
      ctx.save();
      ctx.strokeStyle = config.thresholdColor || '#e0b341';
      ctx.setLineDash([4, 4]);
      ctx.beginPath();
      ctx.moveTo(padL, yAt(config.threshold));
      ctx.lineTo(padL + plotW, yAt(config.threshold));
      ctx.stroke();
      ctx.restore();
    }

    // series
    for (var si = 0; si < config.series.length; si++) {
      var serie = config.series[si];
      var data = serie.data || [];
      if (data.length < 2) continue;

      if (serie.fill) {
        ctx.beginPath();
        var started = false;
        for (var fi = 0; fi < data.length; fi++) {
          if (!Number.isFinite(data[fi])) continue;
          if (!started) { ctx.moveTo(xAt(fi), padT + plotH); started = true; }
          ctx.lineTo(xAt(fi), yAt(data[fi]));
        }
        if (started) {
          ctx.lineTo(xAt(data.length - 1), padT + plotH);
          ctx.closePath();
          ctx.fillStyle = serie.fill;
          ctx.fill();
        }
      }

      ctx.save();
      ctx.strokeStyle = serie.color;
      ctx.lineWidth = serie.width || 1.2;
      if (serie.dashed) ctx.setLineDash([3, 3]);
      ctx.beginPath();

      var pen = false;
      for (var di = 0; di < data.length; di++) {
        var value = data[di];
        if (!Number.isFinite(value)) { pen = false; continue; }
        var px = xAt(di), py = yAt(value);
        if (pen) ctx.lineTo(px, py);
        else { ctx.moveTo(px, py); pen = true; }
      }
      ctx.stroke();
      ctx.restore();
    }

    // legend
    ctx.font = '10px Consolas, monospace';
    var legendX = padL + 4;
    for (var li = 0; li < config.series.length; li++) {
      var item = config.series[li];
      if (!item.label) continue;
      ctx.fillStyle = item.color;
      ctx.fillRect(legendX, padT - 11, 8, 3);
      ctx.fillStyle = '#8b98a6';
      ctx.fillText(item.label, legendX + 12, padT - 7);
      legendX += 14 + ctx.measureText(item.label).width + 12;
    }
  }

  // ---------------------------------------------------------------- cards

  function card(label, value, sub, cls) {
    return '<div class="card ' + (cls || '') + '">' +
      '<div class="label">' + esc(label) + '</div>' +
      '<div class="value">' + value + '</div>' +
      '<div class="sub">' + (sub || '') + '</div>' +
      '</div>';
  }

  function fpsClass(fps, target) {
    if (target && target > 0) {
      if (fps >= target * 0.9) return 'ok';
      if (fps >= target * 0.6) return 'warn';
      return 'bad';
    }
    if (fps >= 55) return 'ok';
    if (fps >= 30) return 'warn';
    return 'bad';
  }

  function renderCards(p) {
    var tpsTarget = p.tpsTarget || 60;
    var html = '';
    html += card('FPS', int(p.fps), '帧 ' + int(p.frames), fpsClass(p.fps, 60));
    html += card('TPS', int(p.tps), '目标 ' + int(p.tpsTarget), fpsClass(p.tps, tpsTarget));
    html += card('帧时间', num(p.avgFrameMs, 2) + ' ms', 'P95 ' + num(p.p95FrameMs, 1) + ' / P99 ' + num(p.p99FrameMs, 1));
    html += card('最慢帧', num(p.maxFrameMs, 1) + ' ms', '窗口内', p.maxFrameMs >= p.thresholdMs ? 'warn' : '');
    html += card('Tick 耗时', num(p.lastTickMs, 2) + ' ms', '峰值 ' + num(p.maxTickMs, 1));
    html += card('托管堆', num(p.heapMB, 0) + ' MB', 'GC 总 ' + int(p.gc0Total + p.gc1Total + p.gc2Total));
    html += card('工作集', num(p.wsMB, 0) + ' MB', '进程物理内存');
    html += card('GC0 / 秒', int(p.gc0), '累计 ' + int(p.gc0Total), p.gc0 >= 20 ? 'warn' : '');
    html += card('GC1 / 秒', int(p.gc1), '累计 ' + int(p.gc1Total), p.gc1 > 0 ? 'warn' : '');
    html += card('GC2 / 秒', int(p.gc2), '累计 ' + int(p.gc2Total), p.gc2 > 0 ? 'bad' : '');
    html += card('尖峰数', int(p.spikesTotal), '保留 ' + int(p.spikesRetained), p.spikesRetained > 0 ? 'warn' : '');
    html += card('会话时长', clock(p.t), 'Tick ' + int(p.tick));

    $('cards').innerHTML = html;
  }

  // ---------------------------------------------------------------- charts

  function renderCharts(p) {
    var threshold = p.thresholdMs || 100;

    var recent = p.frameSeriesRecent || [];
    var recentSeconds = p.fps > 0 ? recent.length / p.fps : 0;
    $('hint-frame').textContent = recent.length ? (recent.length + ' 帧 ≈ ' + num(recentSeconds, 1) + ' 秒') : '';

    drawChart($('cv-frame'), {
      height: 180,
      threshold: threshold,
      series: [
        { data: recent, color: '#4f93bf', fill: 'rgba(79,147,191,0.16)', label: '帧耗时 ms' }
      ]
    });

    var step = p.frameStep || 1;
    var overviewSeconds = p.fps > 0 ? ((p.frameSeries || []).length * step) / p.fps : 0;
    $('hint-overview').textContent = overviewSeconds
      ? ('每点 ' + step + ' 帧，约 ' + num(overviewSeconds / 60, 1) + ' 分钟')
      : '';

    drawChart($('cv-overview'), {
      height: 120,
      threshold: threshold,
      series: [
        { data: p.frameSeries || [], color: '#4f93bf', fill: 'rgba(79,147,191,0.14)', label: '帧耗时 ms' }
      ]
    });

    var hist = p.hist || {};
    drawChart($('cv-fps'), {
      height: 150,
      series: [
        { data: hist.fps || [], color: '#3dc86e', label: 'FPS' },
        { data: hist.tps || [], color: '#4f93bf', label: 'TPS' }
      ]
    });

    drawChart($('cv-mem'), {
      height: 150,
      series: [
        { data: hist.heapMB || [], color: '#e0b341', label: '托管堆 MB' },
        { data: hist.wsMB || [], color: '#4f93bf', label: '工作集 MB' }
      ]
    });

    drawChart($('cv-gc'), {
      height: 150,
      series: [
        { data: hist.gc0 || [], color: '#4f93bf', label: 'GC0' },
        { data: hist.gc1 || [], color: '#e0b341', label: 'GC1' },
        { data: hist.gc2 || [], color: '#e05c4b', label: 'GC2' }
      ]
    });
  }

  // ---------------------------------------------------------------- spikes

  function renderSpikes(p) {
    var body = $('tbl-spikes').querySelector('tbody');
    var rows = (p.spikes || []).slice();

    if (state.spikeSort) {
      rows.sort(function (a, b) {
        var av = a[state.spikeSort], bv = b[state.spikeSort];
        if (typeof av === 'string') return state.spikeSortDesc ? String(bv).localeCompare(av) : String(av).localeCompare(bv);
        return state.spikeSortDesc ? (bv - av) : (av - bv);
      });
    } else {
      rows.reverse();
    }

    $('spike-count').textContent = p.spikesRetained
      ? ('共 ' + p.spikesTotal + ' 次，显示最近 ' + rows.length + ' 条')
      : '尚未记录到超过 ' + num(p.thresholdMs, 0) + 'ms 的帧';

    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="6" class="empty">暂无尖峰</td></tr>';
      return;
    }

    var html = '';
    for (var i = 0; i < rows.length; i++) {
      var s = rows[i];
      var cls = severityClass(s.ms, p.thresholdMs);
      var tickShare = s.ms > 0 ? Math.round((s.tickMs / s.ms) * 100) : 0;
      var methods = s.methods || [];
      var top = methods.length ? methods[0] : null;

      var summary;
      if (top) {
        summary = '<a href="#" class="spike-detail" data-spike="' + s.id + '">' + esc(top.label) + '</a>' +
          ' <span class="dim">' + num(top.ms, 0) + 'ms' +
          (methods.length > 1 ? ' +' + (methods.length - 1) : '') + '</span>';
      } else {
        summary = '<span class="dim">无归因（未开深度分析）</span>';
      }

      html += '<tr>' +
        '<td class="mono">' + clock(s.t) + '</td>' +
        '<td class="num ' + cls + '">' + num(s.ms, 1) + ' ms</td>' +
        '<td class="num">' + num(s.tickMs, 1) + ' ms <span class="dim">(' + tickShare + '%)</span></td>' +
        '<td class="num">' + int(s.fps) + '</td>' +
        '<td class="num">' + num(s.heapMB, 0) + ' MB</td>' +
        '<td>' + summary + '</td>' +
        '</tr>';
    }
    body.innerHTML = html;
  }

  /**
   * Cross-spike rollups: where the stutter time accumulated, by method and by mod.
   * This is the table to read when asking "what keeps hitching".
   */
  function renderSpikeAttribution(p) {
    var methods = p.spikeMethods || [];
    var mods = p.spikeMods || [];

    var hint = methods.length
      ? '共 ' + methods.length + ' 个方法'
      : (p.deep ? '等待尖峰…' : '需要开启深度分析');
    $('attr-method-hint').textContent = hint;
    $('attr-mod-hint').textContent = mods.length ? '共 ' + mods.length + ' 个 Mod' : '';

    var body = $('tbl-spike-methods').querySelector('tbody');
    if (!methods.length) {
      body.innerHTML = '<tr><td colspan="5" class="empty">' +
        (p.deep ? '还没有记录到尖峰。' : '开启深度分析后，尖峰会被归因到具体方法与 Mod。') + '</td></tr>';
    } else {
      var html = '';
      for (var i = 0; i < methods.length; i++) {
        var m = methods[i];
        html += '<tr>' +
          '<td title="' + esc(m.name) + '">' + esc(m.name) + '</td>' +
          '<td class="dim">' + esc(m.mod || '') + '</td>' +
          '<td class="num hot">' + num(m.totalMs, 0) + '</td>' +
          '<td class="num">' + num(m.worstMs, 1) + '</td>' +
          '<td class="num">' + int(m.spikes) + '</td>' +
          '</tr>';
      }
      body.innerHTML = html;
    }

    body = $('tbl-spike-mods').querySelector('tbody');
    if (!mods.length) {
      body.innerHTML = '<tr><td colspan="5" class="empty">' +
        (p.deep ? '还没有记录到尖峰。' : '开启深度分析后，尖峰会被归因到具体方法与 Mod。') + '</td></tr>';
      return;
    }

    var total = 0;
    for (var k = 0; k < mods.length; k++) total += mods[k].totalMs;

    var modHtml = '';
    for (var j = 0; j < mods.length; j++) {
      var mod = mods[j];
      var share = total > 0 ? (mod.totalMs / total) * 100 : 0;
      var shareCls = share >= 40 ? 'hot' : share >= 15 ? 'warm' : '';
      modHtml += '<tr>' +
        '<td>' + esc(mod.name) + '</td>' +
        '<td class="num">' + num(mod.totalMs, 0) + '</td>' +
        '<td class="num">' + num(mod.worstMs, 1) + '</td>' +
        '<td class="num">' + int(mod.spikes) + '</td>' +
        '<td class="num ' + shareCls + '">' + num(share, 1) + '%</td>' +
        '</tr>';
    }
    body.innerHTML = modHtml;
  }

  // ---------------------------------------------------------------- logs

  function logSortValue(log, key) {
    switch (key) {
      case 'avg': return log.avg;
      case 'max': return log.max;
      case 'total': return log.total;
      case 'calls': return log.calls;
      default: return log.percent;
    }
  }

  function renderLogs(p) {
    var body = $('tbl-logs').querySelector('tbody');
    var logs = (p.logs || []).slice();

    if (!logs.length) {
      body.innerHTML = '<tr><td colspan="7" class="empty">' +
        (p.deep ? '正在等待 DPA 采样…' : '未启用深度分析。点击顶部「深度分析」开始逐方法统计。') +
        '</td></tr>';
      $('logs-hint').textContent = p.deep ? ('排序：' + p.sortBy) : '';
      return;
    }

    var filter = state.logFilter;
    if (filter) {
      var needle = filter.toLowerCase();
      logs = logs.filter(function (l) {
        return (l.label || '').toLowerCase().indexOf(needle) >= 0 ||
          (l.mod || '').toLowerCase().indexOf(needle) >= 0;
      });
    }

    logs.sort(function (a, b) { return logSortValue(b, state.logSort) - logSortValue(a, state.logSort); });
    logs = logs.slice(0, 200);

    $('logs-hint').textContent = '共 ' + (p.logs || []).length + ' 条，' + (p.deep ? '采集中' : '未启用');

    var html = '';
    for (var i = 0; i < logs.length; i++) {
      var l = logs[i];
      var pct = (l.percent || 0) * 100;
      var pctCls = pct >= 20 ? 'hot' : pct >= 5 ? 'warm' : '';
      html += '<tr>' +
        '<td title="' + esc(l.key) + '">' + esc(l.label) + (l.pinned ? ' <span class="dim">★</span>' : '') + '</td>' +
        '<td class="dim">' + esc(l.mod || '') + '</td>' +
        '<td class="num ' + pctCls + '">' + num(pct, 1) + '%</td>' +
        '<td class="num">' + num(l.avg, 3) + '</td>' +
        '<td class="num">' + num(l.max, 2) + '</td>' +
        '<td class="num">' + num(l.total, 1) + '</td>' +
        '<td class="num">' + int(l.calls) + '</td>' +
        '</tr>';
    }
    body.innerHTML = html || '<tr><td colspan="7" class="empty">无匹配项</td></tr>';
  }

  function renderMods(p) {
    var body = $('tbl-mods').querySelector('tbody');
    var mods = p.mods || [];

    if (!mods.length) {
      body.innerHTML = '<tr><td colspan="5" class="empty">' +
        (p.deep ? '正在等待 DPA 采样…' : '未启用深度分析。') + '</td></tr>';
      return;
    }

    var html = '';
    for (var i = 0; i < mods.length; i++) {
      var m = mods[i];
      html += '<tr>' +
        '<td>' + esc(m.mod) + '</td>' +
        '<td class="num">' + num(m.total, 1) + '</td>' +
        '<td class="num">' + num(m.max, 2) + '</td>' +
        '<td class="num">' + int(m.calls) + '</td>' +
        '<td class="num dim">' + int(m.methods) + '</td>' +
        '</tr>';
    }
    body.innerHTML = html;
  }

  // ---------------------------------------------------------------- status

  function renderStatus(p) {
    if (p.error) {
      setConn('conn-bad', '序列化错误', p.error);
    } else if (p.inGame) {
      setConn('conn-ok', '已连接');
    } else {
      setConn('conn-wait', '主菜单', '游戏在主菜单，没有可采样的殖民地');
    }

    // TPS is only meaningful while the game is actually advancing - pausing or losing
    // window focus stops the tick loop, and a flat zero would otherwise look like a bug.
    var tickNote = (p.inGame && p.tps === 0) ? ' · 未推进（暂停/失焦）' : '';

    $('session').textContent =
      'HTTP ' + (p.server && p.server.status === 'running' ? (':' + p.server.port) : (p.server ? p.server.status : '?')) +
      ' · ' + num(p.payloadHz, 0) + ' Hz · 阈值 ' + num(p.thresholdMs, 0) + 'ms' + tickNote;

    var btnDeep = $('btn-deep');
    btnDeep.textContent = '深度分析：' + (p.deep ? '开' : '关');
    btnDeep.className = 'btn btn-accent' + (p.deep ? ' btn-active' : '');

    var btnPause = $('btn-pause');
    btnPause.textContent = p.paused ? '恢复采样' : '暂停采样';
    btnPause.className = 'btn' + (p.paused ? ' btn-active' : '');

    $('chk-tick').checked = !!p.deepTick;

    if (document.activeElement !== $('inp-threshold')) {
      $('inp-threshold').value = Math.round(p.thresholdMs);
    }
  }

  function render(p) {
    if (!p || p.seq === undefined) return;
    state.payload = p;
    state.lastMessageAt = Date.now();
    state.connected = true;

    try {
      renderStatus(p);
      renderCards(p);
      renderCharts(p);
      renderSpikes(p);
      renderSpikeAttribution(p);
      renderLogs(p);
      renderMods(p);
    } catch (e) {
      setConn('conn-bad', '渲染错误', e.message);
      console.error('render failed', e);
    }
  }

  // ---------------------------------------------------------------- transport

  var pollTimer = null;

  function setConn(cls, text, title) {
    var conn = $('conn');
    conn.className = 'conn ' + cls;
    conn.textContent = text;
    conn.title = title || '';
  }

  // A malformed payload is NOT a lost connection. Reporting it as one sends you hunting
  // through firewalls and ports when the real fault is a serialisation bug.
  function safeParse(text) {
    try {
      return JSON.parse(text);
    } catch (e) {
      state.connected = false;
      setConn('conn-bad', '数据格式错误',
        '服务器返回的内容不是合法 JSON：' + e.message +
        '\n\n前 200 字符：\n' + String(text).slice(0, 200));
      console.error('payload parse failed', e, text);
      return null;
    }
  }

  function startPolling() {
    if (pollTimer) return;
    pollTimer = setInterval(function () {
      fetch('/api/snapshot', { cache: 'no-store' })
        .then(function (r) {
          if (!r.ok) throw new Error('HTTP ' + r.status);
          return r.text();
        })
        .then(function (text) {
          var payload = safeParse(text);
          if (payload) render(payload);
        })
        .catch(function (e) {
          state.connected = false;
          setConn('conn-bad', '连接断开', e.message);
        });
    }, 500);
  }

  function connect() {
    if (!window.EventSource) { startPolling(); return; }

    var es = new EventSource('/api/stream');

    es.onmessage = function (event) {
      var payload = safeParse(event.data);
      if (!payload) return;
      if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
      render(payload);
    };

    es.onerror = function () {
      state.connected = false;
      setConn('conn-wait', '重连中…');
      // EventSource retries on its own; this is just so a long stall still shows data.
      startPolling();
    };

    // Watchdog: if SSE silently stalls, fall back to polling.
    setInterval(function () {
      if (Date.now() - state.lastMessageAt > 4000) startPolling();
    }, 2000);
  }

  function control(action, value) {
    return fetch('/api/control?action=' + encodeURIComponent(action) + '&value=' + encodeURIComponent(value), { cache: 'no-store' })
      .catch(function () { /* the snapshot will show whether it applied */ });
  }

  // ---------------------------------------------------------------- wiring

  function wire() {
    $('btn-pause').addEventListener('click', function () {
      var paused = state.payload ? !state.payload.paused : false;
      control('pause', paused);
    });

    $('btn-deep').addEventListener('click', function () {
      var deep = state.payload ? !state.payload.deep : true;
      control('deep', deep);
    });

    $('chk-tick').addEventListener('change', function (e) {
      control('tickMode', e.target.checked);
    });

    $('inp-threshold').addEventListener('change', function (e) {
      control('threshold', e.target.value);
    });

    $('btn-clear').addEventListener('click', function () { control('clearSpikes', '1'); });
    $('btn-reset').addEventListener('click', function () { control('reset', '1'); });

    $('sort-logs').addEventListener('change', function (e) {
      state.logSort = e.target.value;
      control('sort', e.target.value);
      if (state.payload) renderLogs(state.payload);
    });

    $('filter-logs').addEventListener('input', function (e) {
      state.logFilter = e.target.value.trim();
      if (state.payload) renderLogs(state.payload);
    });

    $('tbl-spikes').querySelector('thead').addEventListener('click', function (e) {
      var key = e.target.getAttribute && e.target.getAttribute('data-sort');
      if (!key) return;
      if (state.spikeSort === key) state.spikeSortDesc = !state.spikeSortDesc;
      else { state.spikeSort = key; state.spikeSortDesc = true; }
      if (state.payload) renderSpikes(state.payload);
    });

    $('tbl-spikes').addEventListener('click', function (e) {
      var id = e.target.getAttribute && e.target.getAttribute('data-spike');
      if (!id) return;
      e.preventDefault();
      openBreakdown(parseInt(id, 10));
    });

    $('stack-close').addEventListener('click', function () { $('overlay').classList.remove('open'); });
    $('overlay').addEventListener('click', function (e) {
      if (e.target === $('overlay')) $('overlay').classList.remove('open');
    });

    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') $('overlay').classList.remove('open');
    });

    window.addEventListener('resize', function () {
      if (state.payload) renderCharts(state.payload);
    });
  }

  function openBreakdown(id) {
    var p = state.payload;
    if (!p) return;

    var spike = null;
    var list = p.spikes || [];
    for (var i = 0; i < list.length; i++) {
      if (list[i].id === id) { spike = list[i]; break; }
    }
    if (!spike) return;

    $('stack-title').textContent = '尖峰 #' + id + ' · 帧内消耗分解';
    $('stack-body').innerHTML = breakdownHtml(spike);
    $('overlay').classList.add('open');
  }

  /**
   * Shows what DPA's per-method timers say this particular frame spent its time on.
   * This is the useful version of "what caused the spike" - a stack trace taken at the end
   * of the frame only ever contains Root.Update, so it cannot answer the question.
   */
  function breakdownHtml(s) {
    var methods = s.methods || [];
    var mods = s.mods || [];
    var tickShare = s.ms > 0 ? Math.round((s.tickMs / s.ms) * 100) : 0;

    var html = '<div class="bd-head">帧耗时 <strong>' + num(s.ms, 1) + ' ms</strong>' +
      ' · 其中 Tick <strong>' + num(s.tickMs, 1) + ' ms</strong>（' + tickShare + '%）' +
      ' · 会话 ' + clock(s.t) + ' · FPS ' + int(s.fps) + '</div>';

    if (!methods.length) {
      html += '<p class="empty">这一帧没有归因数据。逐方法计时只在开启深度分析后才有。</p>';
      return html;
    }

    html += '<table><thead><tr><th>方法</th><th>Mod</th><th class="num">本帧 ms</th><th class="num">占帧比</th></tr></thead><tbody>';
    for (var i = 0; i < methods.length; i++) {
      var m = methods[i];
      var share = s.ms > 0 ? (m.ms / s.ms) * 100 : 0;
      html += '<tr><td>' + esc(m.label) + '</td>' +
        '<td class="dim">' + esc(m.mod || '') + '</td>' +
        '<td class="num">' + num(m.ms, 2) + '</td>' +
        '<td class="num">' + num(share, 1) + '%</td></tr>';
    }
    html += '</tbody></table>';

    if (mods.length) {
      html += '<div class="bd-head">按 Mod 小计</div>' +
        '<table><thead><tr><th>Mod</th><th class="num">本帧 ms</th></tr></thead><tbody>';
      for (var j = 0; j < mods.length; j++) {
        html += '<tr><td>' + esc(mods[j].mod) + '</td>' +
          '<td class="num">' + num(mods[j].ms, 2) + '</td></tr>';
      }
      html += '</tbody></table>';
    }

    html += '<p class="bd-note">耗时来自 DPA 的逐方法计时，嵌套调用会重复计入，所以合计可能超过帧耗时，看相对占比即可。</p>';
    return html;
  }

  wire();
  connect();
})();
