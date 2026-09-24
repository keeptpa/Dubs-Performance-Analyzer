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
    if (v === null || v === undefined || !isFinite(v)) return '—';
    return Number(v).toFixed(digits === undefined ? 1 : digits);
  }

  function int(v) {
    if (v === null || v === undefined || !isFinite(v)) return '—';
    return Math.round(v).toString();
  }

  function clock(seconds) {
    if (!isFinite(seconds)) return '—';
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
    if (!isFinite(value) || value <= 0) return 1;
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
          if (isFinite(d[k]) && d[k] > yMax) yMax = d[k];
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
          if (!isFinite(data[fi])) continue;
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
        if (!isFinite(value)) { pen = false; continue; }
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
      body.innerHTML = '<tr><td colspan="7" class="empty">暂无尖峰</td></tr>';
      return;
    }

    var html = '';
    for (var i = 0; i < rows.length; i++) {
      var s = rows[i];
      var cls = severityClass(s.ms, p.thresholdMs);
      var tickShare = s.ms > 0 ? Math.round((s.tickMs / s.ms) * 100) : 0;

      html += '<tr>' +
        '<td class="mono">' + clock(s.t) + '</td>' +
        '<td class="num ' + cls + '">' + num(s.ms, 1) + ' ms</td>' +
        '<td class="num">' + num(s.tickMs, 1) + ' ms <span class="dim">(' + tickShare + '%)</span></td>' +
        '<td class="num">' + int(s.fps) + '</td>' +
        '<td class="num">' + num(s.heapMB, 0) + ' MB</td>' +
        '<td class="dim">' + esc(s.top || '—') + '</td>' +
        '<td>' + (s.stack
          ? '<button class="btn stack-btn" data-stack="' + s.stack + '">查看</button>'
          : '<span class="dim">—</span>') +
        '</td>' +
        '</tr>';
    }
    body.innerHTML = html;
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
    var conn = $('conn');

    if (p.error) {
      conn.className = 'conn conn-bad';
      conn.textContent = '序列化错误';
      conn.title = p.error;
    } else if (state.connected) {
      conn.className = p.inGame ? 'conn conn-ok' : 'conn conn-wait';
      conn.textContent = p.inGame ? '已连接' : '主菜单';
      conn.title = p.inGame ? '' : '游戏在主菜单，没有可采样的殖民地';
    }

    $('session').textContent =
      'HTTP ' + (p.server && p.server.status === 'running' ? (':' + p.server.port) : (p.server ? p.server.status : '?')) +
      ' · ' + num(p.payloadHz, 0) + ' Hz · 阈值 ' + num(p.thresholdMs, 0) + 'ms';

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

    if (!state.connected) {
      state.connected = true;
      $('conn').className = 'conn conn-ok';
      $('conn').textContent = '已连接';
    }

    try {
      renderStatus(p);
      renderCards(p);
      renderCharts(p);
      renderSpikes(p);
      renderLogs(p);
      renderMods(p);
    } catch (e) {
      $('conn').className = 'conn conn-bad';
      $('conn').textContent = '渲染错误';
      $('conn').title = e.message;
    }
  }

  // ---------------------------------------------------------------- transport

  var pollTimer = null;

  function startPolling() {
    if (pollTimer) return;
    pollTimer = setInterval(function () {
      fetch('/api/snapshot', { cache: 'no-store' })
        .then(function (r) { return r.json(); })
        .then(render)
        .catch(function () {
          state.connected = false;
          $('conn').className = 'conn conn-bad';
          $('conn').textContent = '连接断开';
        });
    }, 500);
  }

  function connect() {
    if (!window.EventSource) { startPolling(); return; }

    var es = new EventSource('/api/stream');

    es.onmessage = function (event) {
      if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
      try { render(JSON.parse(event.data)); }
      catch (e) { /* a partial frame - the next one will be fine */ }
    };

    es.onerror = function () {
      state.connected = false;
      $('conn').className = 'conn conn-wait';
      $('conn').textContent = '重连中…';
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
      var id = e.target.getAttribute && e.target.getAttribute('data-stack');
      if (!id) return;
      openStack(id);
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

  function openStack(id) {
    $('stack-title').textContent = '尖峰调用栈 #' + id;
    $('stack-body').textContent = '加载中…';
    $('overlay').classList.add('open');

    fetch('/api/stack?id=' + encodeURIComponent(id), { cache: 'no-store' })
      .then(function (r) { return r.text(); })
      .then(function (text) { $('stack-body').textContent = text; })
      .catch(function (e) { $('stack-body').textContent = '加载失败：' + e.message; });
  }

  wire();
  connect();
})();
