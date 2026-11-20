// Небольшие виджеты в духе гаджетов рабочего стола Windows 7: стрелочные часы, спидометр
// процессора и памяти, пятнашки. Рисуются заново, со своим оформлением под текущую тему.

// ---------- стрелочные часы ----------
BD.widget('analog', {
  title: 'widget.analog',
  sizes: [[2, 2], [3, 3]],
  create(body) {
    const ticks = Array.from({ length: 60 }, (_, i) => {
      const major = i % 5 === 0;
      return `<line x1="50" y1="${major ? 6 : 7.5}" x2="50" y2="${major ? 12 : 10}" class="${major ? 'major' : 'minor'}" transform="rotate(${i * 6} 50 50)"/>`;
    }).join('');
    body.innerHTML = `<svg class="analog" viewBox="0 0 100 100">
      <circle cx="50" cy="50" r="47" class="face"/>${ticks}
      <line class="hand hour" x1="50" y1="55" x2="50" y2="27"/>
      <line class="hand minute" x1="50" y1="57" x2="50" y2="15"/>
      <line class="hand second" x1="50" y1="60" x2="50" y2="11"/>
      <circle cx="50" cy="50" r="2.4" class="pin"/></svg>`;
    const hands = { hour: body.querySelector('.hour'), minute: body.querySelector('.minute'), second: body.querySelector('.second') };
    const set = (el, angle) => el.setAttribute('transform', `rotate(${angle} 50 50)`);
    const update = () => {
      const now = new Date();
      const s = now.getSeconds(), m = now.getMinutes() + s / 60, h = (now.getHours() % 12) + m / 60;
      set(hands.hour, h * 30);
      set(hands.minute, m * 6);
      set(hands.second, s * 6); // секундная стрелка — по шагу в секунду, без непрерывной анимации
    };
    update();
    return { update };
  },
});

// ---------- спидометр: процессор и память ----------
BD.widget('meter', {
  title: 'widget.meter',
  sizes: [[4, 2], [2, 2]],
  create(body) {
    // Дуга на 240° — как у стрелочного прибора.
    const dial = (label) => `<div class="meter-dial">
      <svg viewBox="0 0 100 100">
        <path class="meter-track" d="M 21.7 78.3 A 40 40 0 1 1 78.3 78.3"/>
        <path class="meter-fill" d="M 21.7 78.3 A 40 40 0 1 1 78.3 78.3" pathLength="100"/>
        <line class="meter-needle" x1="50" y1="50" x2="50" y2="18"/>
        <circle cx="50" cy="50" r="3.5" class="meter-pin"/>
      </svg>
      <div class="meter-value"></div><div class="meter-label muted">${label}</div></div>`;
    body.innerHTML = dial('CPU') + dial('RAM');
    const dials = [...body.querySelectorAll('.meter-dial')];
    const show = (el, percent) => {
      el.querySelector('.meter-fill').style.strokeDasharray = `${percent} 100`;
      el.querySelector('.meter-needle').style.transform = `rotate(${-120 + percent * 2.4}deg)`;
      el.querySelector('.meter-value').textContent = `${percent}%`;
      el.classList.toggle('high', percent >= 85);
    };
    const onData = (hw) => { show(dials[0], hw.cpu); show(dials[1], hw.memory); };
    BD.on('hw', onData);
    show(dials[0], 0);
    show(dials[1], 0);
    return { destroy() { BD.off('hw', onData); } };
  },
});

// ---------- пятнашки ----------
BD.widget('puzzle', {
  title: 'widget.puzzle',
  sizes: [[3, 3], [4, 4]],
  create(body, context) {
    const N = 4;
    const solved = Array.from({ length: N * N }, (_, i) => (i + 1) % (N * N)); // 1..15, 0 — пустая клетка
    let tiles = (() => { try { return JSON.parse(context.options.tiles); } catch { return null; } })() ?? shuffle();
    let moves = Number(context.options.moves ?? 0);

    body.innerHTML = '<div class="puzzle-head"><span class="muted puzzle-moves"></span><button class="plain puzzle-new"></button></div><div class="puzzle-board"></div>';
    const board = body.querySelector('.puzzle-board');
    const cells = Array.from({ length: N * N - 1 }, (_, i) => {
      const cell = document.createElement('button');
      cell.className = 'puzzle-tile';
      cell.textContent = i + 1;
      cell.addEventListener('click', () => move(i + 1));
      board.appendChild(cell);
      return cell;
    });
    body.querySelector('.puzzle-new').addEventListener('click', () => { tiles = shuffle(); moves = 0; save(); render(); });

    /** Перемешивание случайными ходами — так позиция всегда решаема. */
    function shuffle() {
      const t = solved.slice();
      let empty = t.indexOf(0), last = -1;
      for (let k = 0; k < 200; k++) {
        const options = neighbours(empty).filter((n) => n !== last);
        const pick = options[Math.floor(Math.random() * options.length)];
        [t[empty], t[pick]] = [t[pick], t[empty]];
        last = empty;
        empty = pick;
      }
      return t;
    }
    function neighbours(i) {
      const r = Math.floor(i / N), c = i % N, result = [];
      if (r > 0) result.push(i - N);
      if (r < N - 1) result.push(i + N);
      if (c > 0) result.push(i - 1);
      if (c < N - 1) result.push(i + 1);
      return result;
    }
    function move(value) {
      const at = tiles.indexOf(value), empty = tiles.indexOf(0);
      if (!neighbours(empty).includes(at) || isSolved()) return;
      [tiles[at], tiles[empty]] = [tiles[empty], tiles[at]];
      moves++;
      save();
      render();
    }
    const isSolved = () => tiles.every((v, i) => v === solved[i]);
    const save = () => context.setOptions({ tiles: JSON.stringify(tiles), moves: String(moves) });

    function render() {
      // Плитки двигаются через transform — переход между клетками анимируется в CSS.
      tiles.forEach((value, index) => {
        if (!value) return;
        const cell = cells[value - 1];
        cell.style.transform = `translate(${(index % N) * 100}%, ${Math.floor(index / N) * 100}%)`;
        cell.classList.toggle('home', value === solved[index]);
      });
      body.classList.toggle('solved', isSolved());
      body.querySelector('.puzzle-moves').textContent = isSolved() ? BD.t('widget.puzzle.solved').replace('{n}', moves) : `${BD.t('widget.puzzle.moves')}: ${moves}`;
      body.querySelector('.puzzle-new').textContent = BD.t('widget.puzzle.new');
    }

    render();
    let language = BD.state?.language;
    return { update() { if (BD.state?.language !== language) { language = BD.state?.language; render(); } } };
  },
});
