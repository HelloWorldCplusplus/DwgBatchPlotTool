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
  var psChoices = [];        // 打印样式下拉的公共候选项 [{name, path}] (state.plotStyles + 浏览…选入)
  var psExtra = [];          // 「浏览…」选入的候选 (后端回传前也先显示出来)
  var psSig = '';            // 候选项签名, 变了才重建下拉的 option
  var psGlobal = '';         // 全局打印样式路径, 用于「用全局」这一项的提示
  var psPending = {};        // path -> {v, at}: 列表里刚选、服务端 state 还没回传的值 (防止下拉回弹)
  var gPsPending = null;     // 全局下拉刚选、save-settings 还没回传的值
  var BROWSE = '__browse__';

  function baseName(p) { return String(p || '').split(/[\\\/]/).pop(); }
  function samePath(a, b) { return String(a).toLowerCase() === String(b).toLowerCase(); }

  // 全局当前值还在候选里才显示文件名; 已失效时提示会回退到程序自带的默认样式
  function globalLabel() {
    var known = psGlobal && psChoices.some(function (c) { return samePath(c.path, psGlobal); });
    return '用全局（' + (known ? baseName(psGlobal) : '默认样式') + '）';
  }

  // 逐文件列与全局 CTB 共用这一套下拉: 同源候选 + 同一「浏览…」+ 同一失效兜底。
  // firstLabel = 空值首项文案 (null = 不要空首项); missingLabel = 选中文件找不到时的标注。
  // 只在选项确实变了时重建, 免得 800ms 的状态轮询把正在展开的下拉打断。
  function fillPsOptions(sel, current, firstLabel, missingLabel) {
    var sig = [psSig, psGlobal, current, firstLabel, missingLabel].join('|');
    if (sel.dataset.sig === sig) return;
    sel.dataset.sig = sig;
    var html = firstLabel === null ? '' : '<option value="">' + esc(firstLabel) + '</option>';
    var has = false;
    psChoices.forEach(function (c) {
      if (samePath(c.path, current)) has = true;
      html += '<option value="' + esc(c.path) + '">' + esc(c.name) + '</option>';
    });
    if (current && !has)
      html += '<option value="' + esc(current) + '">' + esc(baseName(current)) + '（' + esc(missingLabel) + '）</option>';
    html += '<option value="' + BROWSE + '">浏览…（其它样式文件）</option>';
    sel.innerHTML = html;
    sel.value = current || '';
    sel.title = current ? current : (firstLabel || '');
  }

  // 全局打印样式下拉: 候选项、浏览项、失效标注与列表列完全一致。
  // 空首项「不指定」对应原先输入框清空的行为。
  function renderGlobalPs(st) {
    var sel = $('ctb');
    if (!sel) return;
    var cur = (st.options && st.options.ctb) ? st.options.ctb : '';
    if (gPsPending) {
      if (gPsPending.v === cur || Date.now() - gPsPending.at > 3000) gPsPending = null;
      else cur = gPsPending.v;
    }
    sel.dataset.cur = cur;
    fillPsOptions(sel, cur, '不指定（按图纸）', '找不到，暂用默认');
    sel.value = cur;
    if (!cur) sel.title = '不指定 CTB，按图纸自带的样式打印';
  }

  // 两个下拉共用的「浏览…」: 选到的文件立刻并入公共候选, 取消则保持原选择。
  // 后端 state 回传前 psExtra 保证这一项不会从下拉里消失。
  function browsePlotStyle(prev) {
    return api('pick-ctb').then(function (r) {
      var p = (r && r.file) || '';
      if (p) {
        var seen = function (c) { return samePath(c.path, p); };
        if (!psChoices.some(seen)) psChoices.push({ name: baseName(p), path: p });
        if (!psExtra.some(seen)) psExtra.push({ name: baseName(p), path: p });
        psSig = JSON.stringify(psChoices);
      }
      return p || prev;
    });
  }

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

  // ---------- 文件列表列宽 (拖表头右边界手工调整) ----------
  // table-layout:fixed 下, 第一行的 th 宽度决定整列宽度。但 CSS 默认里有百分比列,
  // 直接把一列改宽会让浏览器压缩其它百分比列 (越拖越挤)。所以第一次拖动时先把
  // 所有列按当时的实际宽度固化成 px, 之后就只有目标列会变, 多出来的宽度走横向滚动。
  var COL_MIN = 36;
  var colWidths = [];        // 用户设定的每列 px 宽 (全部有值)
  var colLocked = false;     // 是否已固化 (拖过 / 载入过保存值)
  var colDrag = null;        // { i, x, w }

  function colThs() { return document.querySelectorAll('#files thead th'); }

  function applyColWidths() {
    var ths = colThs();
    if (!ths.length) return;
    var table = $('files'), wrap = $('fileWrap');
    if (!colLocked) {
      for (var j = 0; j < ths.length; j++) ths[j].style.width = '';
      table.style.width = '';
      return;
    }
    var sum = 0, i, w;
    for (i = 0; i < ths.length; i++) { w = colWidths[i] || COL_MIN; ths[i].style.width = w + 'px'; sum += w; }
    // 总和不足容器时把差额补给「文件」列: 否则浏览器会把多余空间按比例摊回各列,
    // 拖窄一列反而让其它列一起变宽。总和超出容器就保留, 由 #fileWrap 出横向滚动条。
    var avail = wrap.clientWidth;
    if (sum < avail) { ths[0].style.width = (colWidths[0] + avail - sum) + 'px'; sum = avail; }
    table.style.width = sum + 'px';
  }

  // 第一次拖拽: 记下当前所有列的实际渲染宽度, 之后只改目标列
  function lockColumns() {
    if (colLocked) return;
    var ths = colThs();
    colWidths = [];
    for (var i = 0; i < ths.length; i++)
      colWidths.push(Math.max(COL_MIN, Math.round(ths[i].getBoundingClientRect().width)));
    colLocked = true;
  }

  // 从 settings.ini 恢复: 缺的列用当前默认宽度补齐, 一个有效值都没有就完全用 CSS 默认
  function loadColWidths(s) {
    var ths = colThs(), parts = String(s || '').split(','), vals = [], any = false;
    for (var i = 0; i < ths.length; i++) {
      var n = parseInt(parts[i], 10);
      if (!(n > 0)) n = Math.max(COL_MIN, Math.round(ths[i].getBoundingClientRect().width));
      else any = true;
      vals.push(n);
    }
    colWidths = vals;
    colLocked = any;
    applyColWidths();
  }

  function saveColWidths() {
    return colLocked ? colWidths.join(',') : '';
  }

  function colGuide(on, x) {
    var el = $('colGuide');
    if (!el) {
      el = document.createElement('div');
      el.id = 'colGuide';
      el.className = 'hidden';
      document.body.appendChild(el);
    }
    el.classList.toggle('hidden', !on);
    if (on) el.style.left = Math.round(x) + 'px';
  }

  function initColGrips() {
    var ths = colThs();
    for (var i = 0; i < ths.length; i++) {
      var g = document.createElement('span');
      g.className = 'col-grip';
      g.dataset.col = i;
      g.title = '拖动调整列宽；双击恢复全部默认宽度';
      ths[i].appendChild(g);
    }
    var head = document.querySelector('#files thead');
    head.addEventListener('pointerdown', function (e) {
      var g = e.target.closest ? e.target.closest('.col-grip') : null;
      if (!g || e.button !== 0) return;
      // 只拦冒泡: 不能 preventDefault, 否则 dblclick 不会派发; #files 本来就 user-select:none
      e.stopPropagation();       // 别让 fileWrap 的框选 / 行点击接手
      lockColumns();
      var i = +g.dataset.col;
      colDrag = { i: i, x: e.clientX, w: colWidths[i] };
      colGuide(true, ths[i].getBoundingClientRect().right);
      document.body.classList.add('col-resizing');
    });
    head.addEventListener('dblclick', function (e) {
      var g = e.target.closest ? e.target.closest('.col-grip') : null;
      if (!g) return;
      e.preventDefault(); e.stopPropagation();
      colDrag = null;
      colLocked = false;
      colWidths = [];
      colGuide(false);
      document.body.classList.remove('col-resizing');
      applyColWidths();
      scheduleSave();
    });
    window.addEventListener('pointermove', function (e) {
      if (!colDrag) return;
      colWidths[colDrag.i] = Math.max(COL_MIN, colDrag.w + e.clientX - colDrag.x);
      applyColWidths();
      colGuide(true, ths[colDrag.i].getBoundingClientRect().right);
    });
    var end = function () {
      if (!colDrag) return;
      colDrag = null;
      colGuide(false);
      document.body.classList.remove('col-resizing');
      scheduleSave();          // 400ms 防抖, 拖完一次落盘
    };
    window.addEventListener('pointerup', end);
    window.addEventListener('pointercancel', end);
    window.addEventListener('resize', function () { if (colLocked) applyColWidths(); });
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
    // 列宽只在页面首次拿到设置时恢复一次; 之后 800ms 轮询 / start 回填都不能盖掉用户刚拖的宽度
    if (!window.__filled && opts.colWidths !== undefined) loadColWidths(opts.colWidths);
    Object.keys(opts).forEach(function (k) {
      // ctb 是下拉, 由 renderGlobalPs 建好 option 后再赋值; colWidths 没有对应输入控件
      if (k === 'ctb' || k === 'colWidths') return;
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
    o.colWidths = saveColWidths();   // 随其它参数一起落盘, 下次打开自动恢复
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
    // 本地刚改过全局样式、save-settings 还没回传时, 别让轮询把提示文案闪回旧值
    if (!gPsPending) psGlobal = (st.options && st.options.ctb) ? st.options.ctb : '';
    if (st.plotStyles) {
      var backend = st.plotStyles.slice();
      var extra = psExtra.filter(function (e) {
        return !backend.some(function (b) {
          return String(b.path).toLowerCase() === String(e.path).toLowerCase();
        });
      });
      psChoices = backend.concat(extra);
      psSig = JSON.stringify(psChoices);
    }
    renderGlobalPs(st);        // 全局打印样式与列表列共用同一份候选, 同步构建
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
          '<td class="c7c ps"><select class="ps-in"></select></td>' +
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
        var psSel = tr.children[8].firstChild;
        psSel.addEventListener('click', function (e) { e.stopPropagation(); });
        psSel.addEventListener('change', function () {
          var v = psSel.value;
          if (v === BROWSE) {
            // 下拉里直接选一个不在候选列表里的 .ctb/.stb: 走系统的文件对话框
            psSel.value = tr.dataset.ps || '';   // 弹框期间别停在「浏览…」这一项上
            browsePlotStyle(tr.dataset.ps || '').then(function (nv) {
              psSel.dataset.sig = '';
              fillPsOptions(psSel, nv, globalLabel(), '找不到，暂用全局');
              psSel.value = nv;
              tr.dataset.ps = nv;
              psPending[tr.dataset.path] = { v: nv, at: Date.now() };
              api('set-plot-style', { path: tr.dataset.path, plotStyle: nv });
            });
            return;
          }
          tr.dataset.ps = v;
          psPending[tr.dataset.path] = { v: v, at: Date.now() };
          api('set-plot-style', { path: tr.dataset.path, plotStyle: v });
        });
        var dl = document.createElement('datalist');
        dl.className = 'dlrow';
        dl.id = 'dlrow' + i;
        document.body.appendChild(dl);
        refIn.setAttribute('list', dl.id);
        var chk = tr.children[9].firstChild;
        chk.addEventListener('click', function (e) { e.stopPropagation(); });
        chk.addEventListener('change', function () {
          api('check', { path: tr.dataset.path, checked: chk.checked });
        });
        tr.children[10].firstChild.addEventListener('click', function (e) {
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
      var ps = tr.children[8].firstChild;
      var curPs = it.plotStyle || '';
      var pend = psPending[it.path];
      if (pend) {
        // 服务端已同步 (或等太久) 就丢掉本地值, 否则以用户刚选的值为准
        if (pend.v === curPs || Date.now() - pend.at > 3000) delete psPending[it.path];
        else curPs = pend.v;
      }
      tr.dataset.ps = curPs;
      fillPsOptions(ps, curPs, globalLabel(), '找不到，暂用全局');
      ps.value = curPs;
      tr.children[9].firstChild.checked = (it.checked !== false);
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
  // ctb 是下拉, title 由 fillPsOptions 统一设置, 不参与输入框的截断提示
  var TIP_IDS = ['refName', 'scale', 'mergedName', 'outputDir', 'fontsDir',
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

  // 全局打印样式下拉: 与列表列共用候选项、浏览项与失效兜底, 选完按同样的方式防回弹。
  // select 会同时触发 input 和 change; 两个都得拦住 optPane 的自动存盘,
  // 否则选「浏览…」时哨兵值 __browse__ 会被当成 CTB 路径写进 settings.ini。
  $('ctb').addEventListener('input', function (e) { e.stopPropagation(); });
  $('ctb').addEventListener('change', function (e) {
    e.stopPropagation();
    var sel = $('ctb');
    var v = sel.value;
    if (v === BROWSE) {
      sel.value = sel.dataset.cur || '';   // 别让下拉停在「浏览…」这一项上
      browsePlotStyle(sel.dataset.cur || '').then(function (nv) {
        sel.dataset.sig = '';
        fillPsOptions(sel, nv, '不指定（按图纸）', '找不到，暂用默认');
        sel.value = nv;
        sel.dataset.cur = nv;
        psGlobal = nv;
        gPsPending = { v: nv, at: Date.now() };
        scheduleSave();
      });
      return;
    }
    psGlobal = v;                 // 立即刷新列表列「用全局（xxx）」的提示
    sel.dataset.cur = v;
    gPsPending = { v: v, at: Date.now() };
    scheduleSave();
  });

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

  // ---------- 浏览器原生右键菜单 ----------
  // 程序是用 Edge/Chrome 的 --app= 独立窗口打开的 (HtmlHost.cs 启动浏览器那段), 没有
  // WebView2 宿主可以设 AreDefaultContextMenusEnabled, 所以只能在页面层整页拦掉。
  // 挂 capture 阶段: 比下面 #fileWrap 的 bubble 监听早, 但只 preventDefault、不停止传播,
  // 文件行的自定义批量菜单照常弹出。Shift+右键 是留给用户临时调出原生菜单的通道
  // (调试 / 复制页面文字 / 检查元素), 需要浏览器「返回、刷新、查看源代码」那一套时按着 Shift。
  document.addEventListener('contextmenu', function (e) {
    if (e.shiftKey) return;
    e.preventDefault();
  }, true);

  // ---------- 右键菜单 (对选中的一批文件统一操作) ----------
  // 目标文件在菜单弹出那一刻就锁定成字符串数组: 之后列表刷新、行被重建、选择被改,
  // 都不会让操作落到别的文件上。表单跟菜单共用一个浮层, 不额外弹模态框。
  var ctxPaths = [];
  var ctxKind = '';

  var CTX_FORMS = {
    plotStyle: {
      title: '统一指定打印样式', select: true,
      hint: '留空 = 用全局打印样式；「浏览…」可选任意位置的 CTB / STB。候选项与列表里的下拉完全一样。'
    },
    scale: {
      title: '统一设置比例', placeholder: '1:100 或 100，留空 = 用全局',
      hint: '格式 1:100 或只写分母 100；留空 = 用全局比例（按图框自动算）。格式不对时一个文件都不会改。'
    },
    refName: {
      title: '统一设置参照名', placeholder: '留空 = 用全局参照名', list: 'refList',
      hint: '留空 = 用全局参照名；支持 * ? 通配，下拉里是已识别出来的块名 / 外部参照名。'
    }
  };

  function ctxHide() {
    $('ctxMenu').classList.add('hidden');
    $('ctxItems').classList.remove('hidden');
    $('ctxForm').classList.add('hidden');
    ctxKind = '';
    ctxPaths = [];
  }

  // 菜单贴着光标, 但别超出窗口; 切成表单后尺寸变了要再算一次
  function fitCtx() {
    var m = $('ctxMenu');
    var w = m.offsetWidth, h = m.offsetHeight;
    var x = parseFloat(m.dataset.x || '0'), y = parseFloat(m.dataset.y || '0');
    m.style.left = Math.max(4, Math.min(x, window.innerWidth - w - 8)) + 'px';
    m.style.top = Math.max(4, Math.min(y, window.innerHeight - h - 8)) + 'px';
  }

  function ctxOpen(x, y) {
    var paths = Object.keys(rowByPath).filter(function (p) { return selected[p]; });
    if (!paths.length) return;
    ctxPaths = paths;
    ctxKind = '';
    $('ctxItems').classList.remove('hidden');
    $('ctxForm').classList.add('hidden');
    $('ctxTitle').textContent = '已选 ' + paths.length + ' 个文件';
    $('ctxRemove').textContent = '移出列表（' + paths.length + ' 个）';
    var m = $('ctxMenu');
    m.classList.remove('hidden');
    m.dataset.x = x; m.dataset.y = y;
    fitCtx();
  }

  function ctxShowForm(kind) {
    var f = CTX_FORMS[kind];
    if (!f || !ctxPaths.length) return;
    ctxKind = kind;
    $('ctxItems').classList.add('hidden');
    $('ctxForm').classList.remove('hidden');
    $('ctxFormTitle').textContent = f.title + '（' + ctxPaths.length + ' 个文件）';
    $('ctxFormHint').textContent = f.hint;
    var box = $('ctxFormBody');
    box.innerHTML = '';
    var inp;
    if (f.select) {
      inp = document.createElement('select');
      // 与列表「打印样式」列、全局 CTB 共用同一份候选和同一个「浏览…」
      fillPsOptions(inp, '', globalLabel(), '找不到，暂用全局');
      inp.addEventListener('change', function () {
        if (inp.value !== BROWSE) return;
        var prev = inp.dataset.picked || '';
        inp.value = prev;                       // 弹框期间别停在「浏览…」上
        browsePlotStyle(prev).then(function (nv) {
          inp.dataset.picked = nv;
          inp.dataset.sig = '';
          fillPsOptions(inp, nv, globalLabel(), '找不到，暂用全局');
          inp.value = nv;
          fitCtx();
        });
      });
    } else {
      inp = document.createElement('input');
      inp.type = 'text';
      inp.placeholder = f.placeholder || '';
      if (f.list) inp.setAttribute('list', f.list);   // 复用参数区那份已识别参照名
    }
    inp.className = 'ctx-input';
    box.appendChild(inp);
    fitCtx();
    inp.focus();
  }

  function ctxApply() {
    if (!ctxKind || !ctxPaths.length) return;
    var inp = $('ctxFormBody').firstChild;
    if (!inp) return;
    var v = inp.value, kind = ctxKind, paths = ctxPaths.slice();
    if (kind === 'plotStyle' && v === BROWSE) return;
    var changes = {};
    changes[kind] = v;
    api('batch-set', { paths: paths, changes: changes }).then(function (r) {
      if (r && r.ok === false) { alert(r.error || '设置失败'); return; }
      // 状态轮询回来前, 用本地值压住列表下拉的回弹 (与逐行改样式同一套机制)
      if (kind === 'plotStyle')
        paths.forEach(function (p) { psPending[p] = { v: v, at: Date.now() }; });
      ctxHide();
      refresh();
    });
  }

  function ctxCommand(act) {
    if (!ctxPaths.length) return;
    if (CTX_FORMS[act]) { ctxShowForm(act); return; }
    var changes = null, remove = (act === 'remove');
    if (act === 'clear') changes = { clearOverrides: true };
    else if (act === 'on') changes = { checked: true };
    else if (act === 'off') changes = { checked: false };
    if (!changes && !remove) return;

    var paths = ctxPaths.slice();
    var done = function () {
      if (remove) paths.forEach(function (p) { delete selected[p]; });
      ctxHide();
      refresh();
    };
    if (remove) api('remove', { paths: paths }).then(done);
    else api('batch-set', { paths: paths, changes: changes }).then(function (r) {
      if (r && r.ok === false) { alert(r.error || '设置失败'); return; }
      done();
    });
  }

  var ctxMenu = $('ctxMenu');
  ctxMenu.addEventListener('click', function (e) {
    e.stopPropagation();
    var item = e.target.closest ? e.target.closest('.ctx-item') : null;
    if (item && item.dataset.act) ctxCommand(item.dataset.act);
  });
  ctxMenu.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
  ctxMenu.addEventListener('contextmenu', function (e) { e.preventDefault(); e.stopPropagation(); });
  $('ctxOk').onclick = function (e) { e.stopPropagation(); ctxApply(); };
  $('ctxCancel').onclick = function (e) { e.stopPropagation(); ctxHide(); };
  $('ctxFormBody').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); ctxApply(); }
  });

  // capture 阶段拦 Escape: 菜单开着时只关菜单, 不能让下面那个 handler 顺手清掉选中集
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || ctxMenu.classList.contains('hidden')) return;
    e.preventDefault(); e.stopPropagation();
    ctxHide();
  }, true);

  // 点菜单以外的地方就收起 (capture 阶段不阻止事件, 点行仍然照常选中)
  document.addEventListener('pointerdown', function (e) {
    if (ctxMenu.classList.contains('hidden') || ctxMenu.contains(e.target)) return;
    ctxHide();
  }, true);

  document.getElementById('fileWrap').addEventListener('contextmenu', function (e) {
    // Shift+右键 是留给用户调出浏览器原生菜单的通道, 这里也别弹自己的菜单抢它
    if (e.shiftKey) { ctxHide(); return; }
    // 文本框 / 下拉里不弹自定义菜单; 原生菜单已在上面整页禁用, 复制粘贴用 Ctrl+C / V / X
    if (e.target.closest && e.target.closest('select, input:not([type=checkbox]), textarea, [contenteditable]')) return;
    var tr = e.target.closest ? e.target.closest('tr[data-path]') : null;
    if (!tr) { ctxHide(); return; }
    e.preventDefault();
    var p = tr.dataset.path;
    // 右键落在未选中的行上 = 只操作这一行; 落在已选中的行上 = 保持整片多选
    if (!selected[p]) { selected = {}; selected[p] = true; lastPath = p; paintSel(); }
    ctxOpen(e.clientX, e.clientY);
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
    if (e.target.closest && e.target.closest('input,button,a,select')) return;   // 输入框/按钮上照旧
    dragSel = { x: e.clientX, y: e.clientY, base: (e.ctrlKey || e.metaKey) ? Object.keys(selected) : [] };
    skipClick = false;
  });

  // 在 mousedown 上拦默认行为 (pointerdown 上 preventDefault 会把后面的 click 一起吞掉,
  // 单击选中就失效了); 这里只挡原生选字与拖拽, click 照常派发。
  fileWrap.addEventListener('mousedown', function (e) {
    if (!dragSel) return;
    if (e.target.closest && e.target.closest('input,button,a,select')) return;
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
  initColGrips();
  refresh();
  if (location.hash === '#help') openHelp();
})();

// 本程序开源免费使用，严禁转卖，否则生儿子没屁眼。
