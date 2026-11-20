// Лаунчер: открывается клавишей Win. Поиск, закреплённые программы и список всех программ.
if (BD.role === 'launcher') {
  const $ = (id) => document.getElementById(id);
  const search = $('launcher-search');
  const COLUMNS = 6; // как в base.css

  let apps = [];
  let pinned = [];
  let showSystem = false;
  let icons = {};
  let cells = [];        // ячейки в порядке обхода стрелками
  let selected = 0;
  const requested = new Set();
  let iconQueue = [];
  let iconTimer = null;

  // Иконки подгружаются только для тех программ, что попали на экран: их сотни, а видно три десятка.
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const id = entry.target.dataset.id;
      if (!entry.isIntersecting || icons[id] || requested.has(id)) continue;
      requested.add(id);
      iconQueue.push(id);
    }
    if (iconQueue.length && !iconTimer) {
      iconTimer = setTimeout(() => {
        BD.send({ type: 'launcherIcons', ids: iconQueue });
        iconQueue = [];
        iconTimer = null;
      }, 40);
    }
  }, { root: $('launcher-scroll'), rootMargin: '200px' });

  function cell(app) {
    const el = document.createElement('div');
    el.className = 'launcher-app';
    el.dataset.id = app.id;
    const img = document.createElement('img');
    img.draggable = false;
    if (icons[app.id]) img.src = icons[app.id];
    const label = document.createElement('span');
    label.textContent = app.name;
    el.append(img, label);
    el.title = app.name;
    el.addEventListener('click', () => BD.send({ type: 'launch', id: app.id }));
    // Правая кнопка закрепляет программу в верхней части лаунчера или открепляет её.
    el.addEventListener('contextmenu', () => BD.send({ type: 'launcherPin', id: app.id }));
    observer.observe(el);
    return el;
  }

  function render() {
    const query = search.value.trim().toLowerCase();
    // Служебные утилиты Windows в общем списке не показываются, но поиском находятся.
    let found = apps.filter((a) => showSystem || !a.system);
    if (query) {
      found = apps.filter((a) => a.name.toLowerCase().includes(query));
      // Сначала программы, название которых начинается с набранного.
      found.sort((a, b) => b.name.toLowerCase().startsWith(query) - a.name.toLowerCase().startsWith(query));
    }
    const pinnedApps = query ? [] : pinned.map((id) => apps.find((a) => a.id === id)).filter(Boolean);

    observer.disconnect();
    $('launcher-pinned').replaceChildren(...pinnedApps.map(cell));
    $('launcher-all').replaceChildren(...found.map(cell));
    $('launcher-pinned-title').hidden = $('launcher-pinned').hidden = pinnedApps.length === 0;
    $('launcher-all-title').hidden = Boolean(query) || found.length === 0;
    $('launcher-empty').hidden = found.length > 0;

    cells = [...document.querySelectorAll('.launcher-app')];
    select(0, false);
  }

  function select(index, scroll = true) {
    if (!cells.length) { selected = 0; return; }
    selected = Math.max(0, Math.min(cells.length - 1, index));
    cells.forEach((el, i) => el.classList.toggle('selected', i === selected));
    if (scroll) cells[selected].scrollIntoView({ block: 'nearest' });
  }

  search.addEventListener('input', render);
  // ---------- калькулятор ----------
  // Если в поиске набрано выражение, сверху показывается ответ; Enter копирует его в буфер обмена.
  // Разбор свой, без eval: только числа, + - * / ^ %, скобки, sqrt и pi.
  function calculate(text) {
    const source = text.replace(/,/g, '.').replace(/[×х]/g, '*').replace(/÷/g, '/').replace(/\s+/g, '');
    if (!/[\d)]/.test(source) || !/[-+*/^%(]|sqrt|pi/i.test(source)) return null;
    let pos = 0;
    const peek = () => source[pos];
    const fail = () => { throw new Error('syntax'); };
    function number() {
      const match = /^\d*\.?\d+(e[-+]?\d+)?/i.exec(source.slice(pos));
      if (!match) fail();
      pos += match[0].length;
      return parseFloat(match[0]);
    }
    function primary() {
      if (peek() === '(') { pos++; const v = expression(); if (peek() !== ')') fail(); pos++; return v; }
      if (peek() === '-') { pos++; return -primary(); }
      if (peek() === '+') { pos++; return primary(); }
      if (/^sqrt/i.test(source.slice(pos))) { pos += 4; return Math.sqrt(primary()); }
      if (/^pi/i.test(source.slice(pos))) { pos += 2; return Math.PI; }
      return number();
    }
    function power() {
      let v = primary();
      if (peek() === '%') { pos++; v /= 100; }
      if (peek() === '^') { pos++; v = v ** power(); }
      return v;
    }
    function term() {
      let v = power();
      while (peek() === '*' || peek() === '/') { const op = source[pos++]; const r = power(); v = op === '*' ? v * r : v / r; }
      return v;
    }
    function expression() {
      let v = term();
      while (peek() === '+' || peek() === '-') { const op = source[pos++]; const r = term(); v = op === '+' ? v + r : v - r; }
      return v;
    }
    try {
      const value = expression();
      return pos === source.length && Number.isFinite(value) ? value : null;
    } catch { return null; }
  }

  const calcButton = $('launcher-calc');
  let calcValue = null;
  calcButton.addEventListener('click', () => { if (calcValue != null) BD.send({ type: 'copyText', text: calcValue }); });
  function renderCalc() {
    const value = calculate(search.value.trim());
    const language = BD.state?.language ?? 'en';
    // В буфер — с десятичным разделителем языка интерфейса и без пробелов между разрядами: так число вставится в Excel.
    calcValue = value == null ? null : new Intl.NumberFormat(language, { maximumSignificantDigits: 12, useGrouping: false }).format(value);
    calcButton.hidden = calcValue == null;
    // Пока виден ответ, надпись «Ничего не найдено» не нужна.
    $('launcher-empty').classList.toggle('calc-shown', calcValue != null);
    if (calcValue != null) {
      const shown = new Intl.NumberFormat(language, { maximumSignificantDigits: 12 }).format(value);
      calcButton.innerHTML = `<span class="calc-result"></span><span class="muted calc-hint"></span>`;
      calcButton.querySelector('.calc-result').textContent = `= ${shown}`;
      calcButton.querySelector('.calc-hint').textContent = BD.t('launcher.calcHint');
    }
  }
  search.addEventListener('input', renderCalc);

  document.addEventListener('keydown', (event) => {
    const moves = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: COLUMNS, ArrowUp: -COLUMNS };
    if (event.key in moves) {
      // Стрелки влево и вправо в непустой строке поиска двигают курсор в ней, а не выбор.
      if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && search.value) return;
      event.preventDefault();
      select(selected + moves[event.key]);
    } else if (event.key === 'Enter') {
      // Есть ответ калькулятора — Enter копирует его; иначе запускает выбранную программу.
      if (calcValue != null) BD.send({ type: 'copyText', text: calcValue });
      else if (cells[selected]) BD.send({ type: 'launch', id: cells[selected].dataset.id });
    } else if (event.key === 'Escape') {
      BD.send({ type: 'launcherClose' });
    } else if (document.activeElement !== search) {
      search.focus(); // печатать можно сразу, куда бы ни кликнули до этого
    }
  });

  BD.on('launcher', (data) => { apps = data.apps; pinned = data.pinned; showSystem = Boolean(data.showSystem); render(); });
  BD.on('launcherIcons', (data) => {
    Object.assign(icons, data);
    for (const el of cells) {
      const src = data[el.dataset.id];
      if (src) el.firstChild.src = src;
    }
  });
  BD.on('launcherOpen', () => {
    search.value = '';
    render();
    renderCalc();
    $('launcher-scroll').scrollTop = 0;
    search.focus();
  });
  BD.on('state', render);
}
