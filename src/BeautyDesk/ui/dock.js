// Док: закреплённые и запущенные программы, папки, корзина. Иконки увеличиваются под курсором.
if (BD.role === 'dock') {
  const strip = document.getElementById('dock');
  const elements = new Map(); // id элемента дока -> его узел на странице

  // Размеры в px страницы. Должны совпадать с отступами в base.css.
  const ITEM_PADDING = 8, SEPARATOR_WIDTH = 13, STRIP_PADDING = 16;
  const MAX_SCALE = 1.6;     // во сколько раз вырастает иконка прямо под курсором
  const REACH = 2.6;         // на сколько иконок в стороны расходится увеличение
  const HOVER_MARGIN = 120;  // запас по бокам, куда разъезжается док при увеличении
  const DRAG_THRESHOLD = 8;

  let items = [];
  let icons = {};
  let hovered = false;
  let mouseX = null;
  let drag = null;           // { id, element, startX, moved }
  let suppressClick = false;
  let clipTimer = null;
  let lastClip = '';
  let frame = 0;

  const dockHeight = () => BD.state?.dockHeight ?? 64;
  const dockMargin = () => BD.state?.dockMargin ?? 8;
  const baseSize = () => dockHeight() - 20;
  const isHidden = () => Boolean(BD.state?.dockHidden) && !hovered;

  /** Ширина дока и центры иконок в состоянии покоя — от них считается увеличение, иначе иконки «убегают» от курсора. */
  function restGeometry() {
    const size = baseSize();
    const widths = items.map((item) => (item.kind === 'separator' ? SEPARATOR_WIDTH : size + ITEM_PADDING));
    const width = widths.reduce((sum, w) => sum + w, STRIP_PADDING);
    const left = (window.innerWidth - width) / 2;
    const centers = [];
    let x = left + STRIP_PADDING / 2;
    for (const w of widths) { centers.push(x + w / 2); x += w; }
    return { left, width, centers };
  }

  function layout() {
    const size = baseSize();
    const { centers } = restGeometry();
    const magnify = hovered && mouseX !== null && !drag;
    items.forEach((item, i) => {
      if (item.kind === 'separator') return;
      let scale = 1;
      if (magnify) {
        const distance = Math.abs(mouseX - centers[i]) / (size * REACH);
        if (distance < 1) scale = 1 + (MAX_SCALE - 1) * Math.cos((distance * Math.PI) / 2) ** 2;
      }
      elements.get(item.id)?.style.setProperty('--size', `${size * scale}px`);
    });
  }

  /**
   * Окно дока шире и выше самого дока. Программе сообщается, какая его часть сейчас нужна;
   * остальное она делает «дырой», пропускающей клики к окнам под доком.
   */
  function updateClip() {
    const W = window.innerWidth, H = window.innerHeight;
    const { left, width } = restGeometry();
    let rect;
    if (isHidden()) rect = { x: 0, y: H - 3, width: W, height: 3 };            // полоска у края экрана, чтобы док можно было вызвать
    else if (hovered) rect = { x: left - HOVER_MARGIN, y: 0, width: width + HOVER_MARGIN * 2, height: H };
    else rect = { x: left, y: H - dockMargin() - dockHeight() - 2, width, height: dockHeight() + dockMargin() + 2 };

    const key = JSON.stringify(rect);
    if (key === lastClip) return;
    // Расширяется область сразу, а сужается после того, как иконки доедут на место, — иначе их обрежет на ходу.
    const shrinking = !hovered;
    clearTimeout(clipTimer);
    clipTimer = setTimeout(() => { lastClip = key; BD.send({ type: 'clip', rect }); }, shrinking ? 230 : 0);
  }

  function refresh() {
    document.body.classList.toggle('dock-hidden', isHidden());
    layout();
    updateClip();
  }

  // ---------- содержимое ----------

  function createElement(item) {
    if (item.kind === 'separator') {
      const separator = document.createElement('div');
      separator.className = 'dock-separator';
      return separator;
    }
    const el = document.createElement('div');
    el.className = 'dock-item';
    const img = document.createElement('img');
    img.draggable = false;
    el.appendChild(img);

    const id = item.id;
    el.addEventListener('click', () => {
      if (suppressClick) { suppressClick = false; return; }
      BD.send({ type: 'dock', action: 'click', id });
    });
    el.addEventListener('contextmenu', () => BD.send({ type: 'dock', action: 'menu', id, rect: BD.rect(img) }));
    el.addEventListener('mouseenter', () => {
      if (!drag && el.classList.contains('running')) BD.send({ type: 'dock', action: 'hover', id, rect: BD.rect(img) });
    });
    el.addEventListener('mouseleave', () => BD.send({ type: 'dock', action: 'unhover', id }));
    el.addEventListener('pointerdown', (event) => {
      if (event.button !== 0 || !el.classList.contains('pinned-app')) return;
      drag = { id, element: el, startX: event.clientX, moved: false };
      el.setPointerCapture(event.pointerId);
    });
    el.addEventListener('pointermove', (event) => {
      if (drag?.element !== el) return;
      if (!drag.moved && Math.abs(event.clientX - drag.startX) < DRAG_THRESHOLD) return;
      if (!drag.moved) {
        drag.moved = true;
        el.classList.add('dragging');
        BD.send({ type: 'dock', action: 'unhover', id });
        layout(); // на время перетаскивания увеличение выключается
      }
      // Иконка встаёт перед первой закреплённой программой, чей центр правее курсора.
      const others = [...strip.querySelectorAll('.pinned-app')].filter((x) => x !== el);
      const next = others.find((x) => { const r = x.getBoundingClientRect(); return event.clientX < r.left + r.width / 2; });
      const anchor = next ?? (others.length ? others[others.length - 1].nextSibling : null);
      if (anchor !== el && anchor !== el.nextSibling) strip.insertBefore(el, anchor);
    });
    const endDrag = () => {
      if (drag?.element !== el) return;
      const moved = drag.moved;
      drag = null;
      el.classList.remove('dragging');
      if (!moved) return;
      suppressClick = true; // отпускание после перетаскивания — не клик
      BD.send({ type: 'dock', action: 'reorder', ids: [...strip.querySelectorAll('.pinned-app')].map((x) => x.dataset.id) });
      layout();
    };
    el.addEventListener('pointerup', endDrag);
    el.addEventListener('pointercancel', endDrag);
    return el;
  }

  function render() {
    if (drag?.moved) return; // во время перетаскивания порядок на странице главнее присланного
    const seen = new Set();
    for (const item of items) {
      seen.add(item.id);
      let el = elements.get(item.id);
      if (!el) {
        el = createElement(item);
        el.dataset.id = item.id;
        elements.set(item.id, el);
      }
      if (item.kind !== 'separator') {
        el.classList.toggle('running', item.running);
        el.classList.toggle('active', item.active);
        el.classList.toggle('pinned-app', item.kind === 'app' && item.pinned);
        const title = item.kind === 'trash' ? BD.t('dock.trash') : item.title;
        if (el.title !== title) el.title = title;
        const src = icons[item.icon] ?? '';
        const img = el.firstChild;
        if (img.getAttribute('src') !== src) img.setAttribute('src', src);
      }
      strip.appendChild(el); // заодно ставит в нужном порядке
    }
    for (const [id, el] of elements) {
      if (!seen.has(id)) { el.remove(); elements.delete(id); }
    }
    refresh();
  }

  // ---------- курсор ----------

  // Окно обрезано по доку, поэтому курсор «входит» и «выходит» ровно по его краям.
  document.documentElement.addEventListener('mouseenter', (event) => {
    hovered = true;
    mouseX = event.clientX;
    refresh();
  });
  document.documentElement.addEventListener('mouseleave', () => {
    hovered = false;
    mouseX = null;
    refresh();
  });
  document.addEventListener('mousemove', (event) => {
    mouseX = event.clientX;
    if (!frame) frame = requestAnimationFrame(() => { frame = 0; layout(); });
  });
  window.addEventListener('resize', refresh);

  BD.on('dock', (data) => { items = data.items; render(); });
  BD.on('dockIcons', (data) => { icons = data; render(); });
  BD.on('state', () => { lastClip = ''; render(); });
}
