(function () {
  'use strict';

  var BASE = location.pathname.replace(/[^/]*$/, '');   // /t/<token>/
  var API = BASE + 'api/';
  var $ = function (id) { return document.getElementById(id); };
  var selected = {};        // path -> true
  var lastLogLen = -1;
  var busy = false;
  var refDetails = [];       // 已加入图纸里的块参照明细
  var rowByPath = {};        // path -> 行元素
  var lastPath = '';         // 单击选中的行, 作为 Shift 区间选择的锚点

  function api(action, body) {
    return fetch(API + action, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    }).then(function (r) { return r.json(); });
  }

  function fmtSize(n) {
    if (!n) return '';
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(1) + ' MB';
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  // ---------- 主题 ----------

  var theme = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';

  function applyTheme(t) {
    theme = (t === 'dark') ? 'dark' : 'light';
    document.documentElement.setAttribute('data-theme', theme);
    var b = $('btnTheme');
    if (b) b.textContent = theme === 'dark' ? '☀ 浅色' : '☾ 暗色';
  }

  // ---------- 表单 <-> options ----------

  function fill(opts) {
    if (!opts) return;
    Object.keys(opts).forEach(function (k) {
      var el = $(k);
      if (!el) return;
      var v = opts[k];
      if (el.type === 'checkbox') el.checked = (v === true || v === 'True' || v === 'true' || v === 1);
      else el.value = v;
    });
    var mode = opts['mode'];
    var radios = document.querySelectorAll('input[name=mode]');
    for (var i = 0; i < radios.length; i++) radios[i].checked = (String(radios[i].value) === String(mode));
    syncPaperRow();
  }

  // 纸张选「自定义」时才显示自定义宽高
  function syncPaperRow() {
    var row = $('customPaperRow');
    if (row) row.classList.toggle('hidden', !($('paper') && $('paper').value === '6'));
  }

  function collect() {
    var o = {};
    ['refName', 'scale', 'offsetX', 'offsetY', 'customW', 'customH',
      'mergedName', 'outputDir', 'ctb', 'timeout', 'space', 'paper', 'lineweight', 'orientation',
      'frameOrder',
    'dpi',
      'fontsDir'
    ].forEach(function (k) { o[k] = $(k) ? $(k).value : ''; });
    ['center', 'fit', 'mono', 'plotstyles', 'hidden'].forEach(function (k) {
      o[k] = $(k) ? $(k).checked : false;
    });
    var m = document.querySelector('input[name=mode]:checked');
    o.mode = m ? m.value : '0';
    return o;
  }

  // ---------- 渲染 ----------

  // 鼠标悬停显示完整内容 (内容短时也提示, 免得用户去判断有没有被截断)
  function tip(el, text) {
    if (!el) return;
    var t = (text === undefined || text === null) ? el.textContent : text;
    el.title = (t === '') ? '' : t;
  }

  function render(st) {
    var tb = document.querySelector('#files tbody');
    if (tb.childElementCount !== st.items.length) {
      tb.innerHTML = '';
      var old = document.querySelectorAll('datalist.dlrow');
      for (var q = 0; q < old.length; q++) old[q].remove();
      rowByPath = {};
      st.items.forEach(function (it, i) {
        var tr = document.createElement('tr');
        tr.dataset.path = it.path;
        rowByPath[it.path] = tr;
        tr.innerHTML = '<td class="c1"></td><td class="c2"></td><td class="c3 num"></td>' +
          '<td class="c4"></td><td class="c5 num"></td><td class="c6 out"></td>' +
          '<td class="c7 ref"><input class="ref-in" placeholder="用全局"></td>' +
          '<td class="c7b scale"><input class="scale-in" placeholder="用全局"></td>' +
          '<td class="c8 chk"><input type="checkbox" checked></td>' +
          '<td class="c9"><button class="row-del" title="移除此行">✕</button></td>';
        tr.addEventListener('click', function (e) {
          if (skipClick) { skipClick = false; return; }   // 刚框选过, 这次 click 忽略
          if (e.shiftKey && lastPath && rowByPath[lastPath]) {
            // Shift 点选: 从锚点行到这里整段选中 (按住 Ctrl 则加到已有选择上)
            var names = Object.keys(rowByPath);
            var a = names.indexOf(lastPath), b = names.indexOf(it.path);
            if (a >= 0 && b >= 0) {
              if (!e.ctrlKey) selected = {};
              for (var k = Math.min(a, b); k <= Math.max(a, b); k++) selected[names[k]] = true;
              paintSel();
              return;      // 锚点不动, 便于反复调整范围
            }
          }
          if (e.ctrlKey) selected[it.path] = !selected[it.path];
          else { selected = {}; selected[it.path] = true; }
          lastPath = it.path;
          paintSel();
        });
        tr.children[5].addEventListener('click', function (e) {
          e.stopPropagation();
          var p = tr.dataset.out;
          if (!p) { alert('这个文件还没有生成输出'); return; }
          api('open-file', { path: p }).then(function (r) {
            if (r && r.ok === false) alert(r.error || '打不开该文件');
          });
        });
        var refIn = tr.children[6].firstChild;
        refIn.addEventListener('click', function (e) { e.stopPropagation(); });
        refIn.addEventListener('change', function () {
          api('set-ref', { path: tr.dataset.path, refName: refIn.value });
        });
        var scaleIn = tr.children[7].firstChild;
        scaleIn.addEventListener('click', function (e) { e.stopPropagation(); });
        scaleIn.addEventListener('change', function () {
          api('set-scale', { path: tr.dataset.path, scale: scaleIn.value }).then(function (r) {
            if (r && r.ok === false) { alert(r.error || '比例格式不对'); scaleIn.value = tr.dataset.scale; }
          });
        });
        var dl = document.createElement('datalist');
        dl.className = 'dlrow';
        dl.id = 'dlrow' + i;
        document.body.appendChild(dl);
        refIn.setAttribute('list', dl.id);
        var chk = tr.children[8].firstChild;
        chk.addEventListener('click', function (e) { e.stopPropagation(); });
        chk.addEventListener('change', function () {
          api('check', { path: tr.dataset.path, checked: chk.checked });
        });
        tr.children[9].firstChild.addEventListener('click', function (e) {
          e.stopPropagation();
          delete selected[tr.dataset.path];
          api('remove', { paths: [tr.dataset.path] });
        });
        tb.appendChild(tr);
      });
    }
    var rows = tb.children;
    var nOk = 0, nErr = 0;
    st.items.forEach(function (it, i) {
      var tr = rows[i];
      var ext = (String(it.name).split('.').pop() || '').toLowerCase();
      var tag = (ext === 'dwg' || ext === 'dxf')
        ? '<span class="ft ' + ext + '">' + ext.toUpperCase() + '</span>' : '';
      tr.children[0].innerHTML = tag + esc(it.name);
      tr.children[1].textContent = it.dir;
      tr.children[2].textContent = fmtSize(it.size);
      var s = it.status || '';
      var cls = s.indexOf('失败') === 0 ? 'err'
        : s.indexOf('成功') === 0 ? 'ok'
          : (s.indexOf('待处理') === 0 ? '' : 'warn');
      if (cls === 'ok') nOk++; else if (cls === 'err') nErr++;
      tr.children[3].innerHTML = '<span class="badge' + (cls ? ' ' + cls : '') + '">' + esc(s) + '</span>';
      tr.children[4].textContent = it.elapsed ? (it.elapsed + ' ms') : '';
      tr.children[5].textContent = it.out || '';
      tr.dataset.out = it.out || '';
      var ri = tr.children[6].firstChild;
      if (document.activeElement !== ri) ri.value = it.refName || '';
      var sc = tr.children[7].firstChild;
      tr.dataset.scale = it.scale || '';
      if (document.activeElement !== sc) sc.value = tr.dataset.scale;
      var dl = document.getElementById('dlrow' + i);
      if (dl) {
        var names = [];
        for (var k = 0; k < refDetails.length; k++)
          if (refDetails[k].file === it.path) names.push(refDetails[k].name);
        var html = names.map(function (n) { return '<option value="' + esc(n) + '"></option>'; }).join('');
        if (dl.innerHTML !== html) dl.innerHTML = html;
      }
      tr.children[8].firstChild.checked = (it.checked !== false);
      for (var c = 0; c < 6; c++) tip(tr.children[c], tr.children[c].textContent);
    });
    paintSel();
    $('emptyHint').style.display = st.items.length ? 'none' : '';

    var pct = st.total ? Math.round(st.done * 100 / st.total) : 0;
    $('bar').style.width = pct + '%';
    $('status').textContent = st.summary || '就绪';
    var cnt = [];
    if (st.total) cnt.push(st.done + ' / ' + st.total + ' 个文件');
    if (nOk) cnt.push('成功 ' + nOk);
    if (nErr) cnt.push('失败 ' + nErr);
    $('statCounts').textContent = cnt.join('  ·  ');
    var dot = $('statusDot');
    if (dot) dot.className = 'dot' + (st.running ? ' run' : (nErr ? ' err' : (nOk ? ' ok' : '')));

    if (st.log.length !== lastLogLen) {
      lastLogLen = st.log.length;
      var log = $('log');
      log.innerHTML = st.log.map(function (l) {
        var m = /^(\d{2}:\d{2}:\d{2})\s([\s\S]*)$/.exec(l);
        return m
          ? '<div class="logline"><span class="t">' + m[1] + '</span>' + esc(m[2]) + '</div>'
          : '<div class="logline">' + esc(l) + '</div>';
      }).join('');
      log.scrollTop = log.scrollHeight;
    }

    var dl = $('refList');
    if (dl.childElementCount !== st.refNames.length) {
      dl.innerHTML = '';
      st.refNames.forEach(function (n) {
        var o = document.createElement('option');
        o.value = n;
        dl.appendChild(o);
      });
    }

    var hint = $('refHint');
    if (hint) {
      var ht = '', hc = '';
      if (st.refScanning) ht = '正在识别参照名…';
      else if (st.refError) { ht = '识别失败: ' + st.refError; hc = 'err'; }
      else if (st.items.length && st.refNames.length) { ht = '已识别 ' + st.refNames.length + ' 个参照名 (下拉选择, 支持 * ? 通配)'; hc = 'ok'; }
      else if (st.items.length) ht = '这些图纸里没识别到参照名, 可直接手动输入';
      else ht = '加入图纸后自动识别参照名';
      hint.textContent = ht;
      hint.className = hc;
      hint.title = ht;
    }

    if (st.refDetails) {
      refDetails = st.refDetails;
      if (!$('refPanel').classList.contains('hidden')) paintRefs();
    }

    $('btnStart').disabled = st.running || st.items.length === 0;
    $('btnStop').disabled = !st.running;
    $('engineInfo').textContent = st.engine ? ('引擎: ' + st.engineExe) : '未找到 ODA 引擎';
    $('engineInfo').title = st.engine ? st.engineExe : '';
    tip($('engineInfo'), st.engine ? st.engineExe : '');
    if (st.options && !window.__filled) { fill(st.options); window.__filled = true; }
    tipInputs();
  }

  // 输入框内容被截断时, 悬停显示完整内容
  var TIP_IDS = ['refName', 'scale', 'mergedName', 'outputDir', 'ctb', 'fontsDir',
    'timeout', 'customW', 'customH', 'offsetX', 'offsetY'];
  function tipInputs() {
    TIP_IDS.forEach(function (id) { tip($(id), $(id) ? $(id).value : ''); });
  }

  function paintSel() {
    var rows = document.querySelectorAll('#files tbody tr');
    for (var i = 0; i < rows.length; i++) {
      rows[i].classList.toggle('sel', !!selected[rows[i].dataset.path]);
    }
  }

  // 参照名明细表 (参照名/所在图纸/实例数/空间/尺寸), 点一行即回填
  function paintRefs() {
    var key = $('refFilter').value.trim();
    var tb = document.querySelector('#refTable tbody');
    tb.innerHTML = '';
    refDetails.forEach(function (r) {
      if (key && String(r.name).toLowerCase().indexOf(key.toLowerCase()) < 0) return;
      var tr = document.createElement('tr');
      tr.innerHTML = '<td></td><td></td><td></td><td></td><td></td>';
      // 外部参照(被参照的 dwg/dxf)用紫色标签标出来, 和块名区分开
      tr.children[0].innerHTML = esc(r.name) + (r.xref ? ' <span class="ft xref">外部参照</span>' : '');
      tr.children[1].textContent = (r.file || '').split(/[\\\/]/).pop();
      tr.children[2].textContent = r.count;
      tr.children[3].textContent = r.space;
      tr.children[4].textContent = r.w + ' × ' + r.h;
      for (var c = 0; c < 5; c++) tip(tr.children[c], tr.children[c].textContent);
      tr.addEventListener('click', function () {
        // 选中行时填到那一行的参照名, 没选中就填全局参照名
        var sel = Object.keys(selected);
        if (sel.length === 1 && rowByPath[sel[0]]) {
          var inp = rowByPath[sel[0]].children[6].firstChild;
          inp.value = r.name;
          api('set-ref', { path: sel[0], refName: r.name });
        } else {
          $('refName').value = r.name;
        }
        $('refPanel').classList.add('hidden');
      });
      tb.appendChild(tr);
    });
  }

  function refresh() {
    if (dragSel) return;      // 框选中不重建列表, 免得行被换掉、列表闪动
    fetch(API + 'state').then(function (r) { return r.json(); })
      .then(render)
      .catch(function () { });
  }

  // ---------- 事件 ----------

  TIP_IDS.forEach(function (id) {
    var el = $(id);
    if (el) el.addEventListener('input', function () { tip(el, el.value); });
  });

  $('btnAddFiles').onclick = function () {
    busy = true;
    api('pick-files').then(function (r) {
      busy = false;
      if (r && r.paths && r.paths.length) api('add', { paths: r.paths }).then(refresh);
    });
  };

  $('btnAddFolder').onclick = function () {
    busy = true;
    api('pick-folder').then(function (r) {
      busy = false;
      if (r && r.dir) api('add', { dir: r.dir, recursive: true }).then(refresh);
    });
  };

  $('btnRemove').onclick = function () {
    var paths = Object.keys(selected).filter(function (p) { return selected[p]; });
    if (!paths.length) { alert('请先在列表里选择要移除的文件'); return; }
    api('remove', { paths: paths }).then(function () { selected = {}; refresh(); });
  };

  $('btnClear').onclick = function () {
    api('clear').then(function () { selected = {}; refresh(); });
  };

  $('btnCtb').onclick = function () {
    api('pick-ctb').then(function (r) { if (r && r.file) { $('ctb').value = r.file; scheduleSave(); } });
  };

  $('btnFontsDir').onclick = function () {
    api('pick-folder').then(function (r) { if (r && r.dir) { $('fontsDir').value = r.dir; scheduleSave(); } });
  };

  $('btnRefList').onclick = function () {
    var p = $('refPanel');
    if (p.classList.contains('hidden')) { p.classList.remove('hidden'); paintRefs(); }
    else p.classList.add('hidden');
  };

  $('refFilter').oninput = paintRefs;

  $('btnRescan').onclick = function () { api('rescan').then(function () { refresh(); }); };

  $('paper').onchange = syncPaperRow;

  $('btnTheme').onclick = function () {
    applyTheme(theme === 'dark' ? 'light' : 'dark');
    var o = collect();
    o.theme = theme;
    api('save-settings', { options: o });
  };

  // 参数一改动就自动落盘（防抖 400ms）：这样不管用什么方式退出程序，设置都已经保存好了
  var saveTimer = null;
  function scheduleSave() {
    if (!window.__filled) return;      // 页面还没拿到服务端设置前不落盘, 否则会把设置存成空
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(function () {
      saveTimer = null;
      api('save-settings', { options: collect() });
    }, 400);
  }
  var optPane = $('optPane');
  if (optPane) {
    optPane.addEventListener('input', scheduleSave);
    optPane.addEventListener('change', scheduleSave);
  }

  $('btnOutDir').onclick = function () {
    api('pick-folder').then(function (r) { if (r && r.dir) { $('outputDir').value = r.dir; scheduleSave(); } });
  };

  $('btnOpenOut').onclick = function () {
    api('open-dir', { dir: $('outputDir').value });
  };

  $('btnStart').onclick = function () {
    api('start', { options: collect() }).then(function (r) {
      if (!r.ok) { alert(r.error || '启动失败'); return; }
      if (r.options) fill(r.options);      // 回写规范化后的值 (比例 2 -> 1:2)
      refresh();
    });
  };

  $('btnHelp').onclick = function () { openHelp(); };

  // ---------- 帮助 ----------

  var helpLoaded = false;

  function openHelp() {
    var m = $('helpModal');
    if (!m) return;
    m.classList.remove('hidden');
    if (helpLoaded) return;
    helpLoaded = true;
    fetch(BASE + 'help.html').then(function (r) { return r.text(); })
      .then(function (html) { $('helpBody').innerHTML = html; })
      .catch(function () { helpLoaded = false; $('helpBody').textContent = '帮助内容加载失败。'; });
  }

  function closeHelp() {
    var m = $('helpModal');
    if (m) m.classList.add('hidden');
  }

  $('btnHelpClose').onclick = closeHelp;
  $('btnHelpOk').onclick = closeHelp;
  $('helpModal').addEventListener('click', function (e) {
    if (e.target === $('helpModal')) closeHelp();   // 点遮罩关闭
  });

  $('btnStop').onclick = function () { api('cancel').then(refresh); };

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      if ($('helpModal') && !$('helpModal').classList.contains('hidden')) { closeHelp(); return; }
      selected = {}; paintSel();
    }
  });

  // ---------- 框选 (左键拖出矩形, 落到矩形里的行都选中) ----------
  var dragSel = null;          // { x, y, base } 拖拽起点与按下 Ctrl 时的原有选择
  var skipClick = false;       // 拖过之后紧跟的那次 click 要吃掉, 否则会退化成单选

  function marquee() {
    var el = document.getElementById('marquee');
    if (!el) {
      el = document.createElement('div');
      el.id = 'marquee';
      el.className = 'hidden';
      document.body.appendChild(el);
    }
    return el;
  }

  function rowsInRect(x1, y1, x2, y2) {
    var l = Math.min(x1, x2), t = Math.min(y1, y2);
    var r = Math.max(x1, x2), b = Math.max(y1, y2);
    var hit = [];
    Object.keys(rowByPath).forEach(function (p) {
      var q = rowByPath[p].getBoundingClientRect();
      if (q.right >= l && q.left <= r && q.bottom >= t && q.top <= b) hit.push(p);
    });
    return hit;
  }

  // 用 pointer 事件 + 指针捕获: 拖到窗口外也照样收到 move/up, 并且 preventDefault 挡掉
  // 浏览器自己的选字与自动滚动 (之前用 mousedown/mousemove, 实际拖起来页面会跟着滚)。
  var fileWrap = document.getElementById('fileWrap');

  function dragMove(e) {
    if (!dragSel) return;
    var dx = e.clientX - dragSel.x, dy = e.clientY - dragSel.y;
    if (!skipClick && Math.abs(dx) < 3 && Math.abs(dy) < 3) return;       // 抖一下不算拖
    skipClick = true;
    var box = marquee();
    box.classList.remove('hidden');
    box.style.left = Math.min(dragSel.x, e.clientX) + 'px';
    box.style.top = Math.min(dragSel.y, e.clientY) + 'px';
    box.style.width = Math.abs(dx) + 'px';
    box.style.height = Math.abs(dy) + 'px';
    selected = {};
    dragSel.base.forEach(function (p) { selected[p] = true; });
    rowsInRect(dragSel.x, dragSel.y, e.clientX, e.clientY).forEach(function (p) { selected[p] = true; });
    paintSel();
  }

  function dragEnd() {
    if (!dragSel) return;
    var dragged = skipClick;
    dragSel = null;
    marquee().classList.add('hidden');
    if (dragged) lastPath = '';   // 框选后不保留 Shift 锚点
  }

  fileWrap.addEventListener('pointerdown', function (e) {
    if (e.button !== 0) return;
    if (e.target.closest && e.target.closest('input,button,a')) return;   // 输入框/按钮上照旧
    dragSel = { x: e.clientX, y: e.clientY, base: (e.ctrlKey || e.metaKey) ? Object.keys(selected) : [] };
    skipClick = false;
  });

  // 在 mousedown 上拦默认行为 (pointerdown 上 preventDefault 会把后面的 click 一起吞掉,
  // 单击选中就失效了); 这里只挡原生选字与拖拽, click 照常派发。
  fileWrap.addEventListener('mousedown', function (e) {
    if (!dragSel) return;
    if (e.target.closest && e.target.closest('input,button,a')) return;
    e.preventDefault();
  });

  // move/up 挂在 window 上: 指针移出列表甚至移出窗口也不丢事件, 又不像 setPointerCapture
  // 那样把 click 的目标改到列表容器上 (那样行点击就全废了)。
  window.addEventListener('pointermove', dragMove);
  window.addEventListener('pointerup', dragEnd);
  window.addEventListener('pointercancel', dragEnd);

  document.addEventListener('selectstart', function (e) {
    if (dragSel) e.preventDefault();
  });

  window.addEventListener('click', function () { skipClick = false; }, false);

  // ---------- 拖拽添加 ----------
  // 浏览器只给 File 对象、拿不到磁盘路径, 所以先把内容传到程序里落到临时目录,
  // 再按「添加文件夹」递归收集一次 —— 子目录结构由临时目录里的层级还原。
  var dropDepth = 0;

  function hasFiles(e) {
    var t = e.dataTransfer && e.dataTransfer.types;
    return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0;
  }

  function walkEntry(entry, prefix, out, done) {
    if (entry.isFile) {
      entry.file(function (f) { out.push({ file: f, rel: prefix + entry.name }); done(); }, done);
    } else if (entry.isDirectory) {
      var reader = entry.createReader(), all = [];
      (function read() {
        reader.readEntries(function (ents) {
          if (!ents.length) {
            var left = all.length;
            if (!left) { done(); return; }
            all.forEach(function (e) {
              walkEntry(e, prefix + entry.name + '/', out, function () { if (--left === 0) done(); });
            });
            return;
          }
          all = all.concat(Array.prototype.slice.call(ents));
          read();
        }, done);
      })();
    } else done();
  }

  function collectDropped(dt, done) {
    var items = dt.items ? Array.prototype.slice.call(dt.items) : [];
    var out = [], settled = false, pending = 0;
    function step() { if (!settled && pending === 0 && out.length) { settled = true; done(out); } }
    function track() { pending++; return function () { pending--; step(); }; }
    items.forEach(function (it) {
      var entry = it.webkitGetAsEntry ? it.webkitGetAsEntry() : null;
      if (entry) { walkEntry(entry, '', out, track()); return; }
      var f = it.getAsFile ? it.getAsFile() : null;   // 拿不到 entry 时退回 File
      if (f) out.push({ file: f, rel: f.name });
    });
    if (!items.length) {
      Array.prototype.slice.call(dt.files || []).forEach(function (f) { out.push({ file: f, rel: f.name }); });
    }
    step();
  }

  function uploadDropped(list) {
    var files = list.filter(function (x) { return /\.(dwg|dxf)$/i.test(x.rel); });
    if (!files.length) { $('status').textContent = '拖进来的不是 dwg / dxf 文件'; return; }
    var root = '';
    (function next(i) {
      if (i >= files.length) {
        api('add', { dir: root }).then(function () { selected = {}; refresh(); });
        return;
      }
      $('status').textContent = '正在接收拖入的文件 ' + (i + 1) + '/' + files.length + ' …';
      fetch(API + 'upload', {
        method: 'POST',
        headers: { 'X-Name': encodeURIComponent(files[i].rel) },
        body: files[i].file
      })
        .then(function (r) { return r.json(); })
        .then(function (j) {
          if (!j || !j.ok) throw new Error((j && j.error) || '接收失败');
          root = j.root;
          next(i + 1);
        })
        .catch(function (err) { $('status').textContent = '拖拽添加失败: ' + err.message; });
    })(0);
  }

  window.addEventListener('dragenter', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault(); dropDepth++; $('app').classList.add('dragover');
  });
  window.addEventListener('dragover', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault(); e.dataTransfer.dropEffect = 'copy';
  });
  window.addEventListener('dragleave', function (e) {
    if (!hasFiles(e)) return;
    dropDepth = Math.max(0, dropDepth - 1);
    if (!dropDepth) $('app').classList.remove('dragover');
  });
  window.addEventListener('drop', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault(); dropDepth = 0; $('app').classList.remove('dragover');
    collectDropped(e.dataTransfer, uploadDropped);
  });

  window.addEventListener('beforeunload', function () {
    try {
      navigator.sendBeacon(API + 'bye');
      // 关窗口时也把当前参数存下来 (页面还没拿到设置就别存, 否则会存成空)
      if (window.__filled)
        navigator.sendBeacon(API + 'save-settings',
          new Blob([JSON.stringify({ options: collect() })], { type: 'application/json' }));
    } catch (e) { }
  });

  setInterval(function () { fetch(API + 'ping').catch(function () { }); }, 5000);
  setInterval(refresh, 800);
  applyTheme(theme);
  refresh();
  if (location.hash === '#help') openHelp();
})();
