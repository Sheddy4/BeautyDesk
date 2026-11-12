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
  let hovered = false;       // курсор над доком
  let held = false;          // открыто превью окон: док не должен прятаться, пока человек выбирает окно
  let mouseX = null;
  let press = null;          // нажатие на иконку: { id, element, startX, dragging }
  let clip = null;           // область окна, которую программа сейчас оставила видимой
  let clipTimer = null;
  let pendingClip = '';
  let frame = 0;

  const dockHeight = () => BD.state?.dockHeight ?? 64;
  const dockMargin = () => BD.state?.dockMargin ?? 8;
  const baseSize = () => dockHeight() - 20;
  const isHidden = () => Boolean(BD.state?.dockHidden) && !hovered && !held;
  const dragging = () => Boolean(press?.dragging);

  /** Ширина дока и центры иконок в состоянии покоя — от них считается увеличение, иначе иконки «убегают» от курсора. */
  function restGeometry() {
    const size = baseSize();
    const order = [...strip.children].map((el) => el.dataset.kind);
    const widths = order.map((kind) => (kind === 'separator' ? SEPARATOR_WIDTH : size + ITEM_PADDING));
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
    const magnify = hovered && mouseX !== null && !dragging();
    [...strip.children].forEach((el, i) => {
      if (el.dataset.kind === 'separator') return;
      let scale = 1;
      if (magnify) {
        const distance = Math.abs(mouseX - centers[i]) / (size * REACH);
        if (distance < 1) scale = 1 + (MAX_SCALE - 1) * Math.cos((distance * Math.PI) / 2) ** 2;
      }
      el.style.setProperty('--size', `${size * scale}px`);
    });
  }

  /**
   * Окно дока шире и выше самого дока. Программе сообщается, какая его часть сейчас нужна;
   * остальное она делает «дырой», пропускающей клики к окнам под доком.
   */
  function updateClip() {
    const H = window.innerHeight;
    const { left, width } = restGeometry();
    let rect;
    // Спрятанный док оставляет полоску у края экрана, чтобы его можно было вызвать. Она той же ширины,
    // что и область под курсором: будь она шире, док у дальнего края экрана выезжал бы и тут же прятался.
    if (isHidden()) rect = { x: left - HOVER_MARGIN, y: H - 3, width: width + HOVER_MARGIN * 2, height: 3 };
    else if (hovered) rect = { x: left - HOVER_MARGIN, y: 0, width: width + HOVER_MARGIN * 2, height: H };
    else rect = { x: left, y: H - dockMargin() - dockHeight() - 2, width, height: dockHeight() + dockMargin() + 2 };

    const key = JSON.stringify(rect);
    if (key === pendingClip) return;
    pendingClip = key;
    // Расширяется область сразу, а сужается после того, как иконки доедут на место, — иначе их обрежет на ходу.
    clearTimeout(clipTimer);
    clipTimer = setTimeout(() => { clip = rect; BD.send({ type: 'clip', rect }); }, hovered ? 0 : 230);
  }

  function refresh() {
    document.body.classList.toggle('dock-hidden', isHidden());
    layout();
    updateClip();
  }

  function setHovered(value, x = null) {
    hovered = value;
    mouseX = value ? x : null;
    refresh();
  }

  // ---------- содержимое ----------

  function createElement(item) {
    const el = document.createElement('div');
    el.dataset.id = item.id;
    el.dataset.kind = item.kind;
    if (item.kind === 'separator') {
      el.className = 'dock-separator';
      return el;
    }
    el.className = 'dock-item';
    const img = document.createElement('img');
    img.draggable = false;
    el.appendChild(img);

    const id = item.id;
    el.addEventListener('contextmenu', () => BD.send({ type: 'dock', action: 'menu', id, rect: BD.rect(img) }));
    el.addEventListener('mouseenter', () => {
      if (!press && el.classList.contains('running')) BD.send({ type: 'dock', action: 'hover', id, rect: BD.rect(img) });
    });
    el.addEventListener('mouseleave', () => BD.send({ type: 'dock', action: 'unhover', id }));
    el.addEventListener('pointerdown', (event) => {
      if (event.button !== 0) return;
      press = { id, element: el, startX: event.clientX, dragging: false };
      // Мышь захватывает сам док, а не иконка: иконка во время перетаскивания переезжает по странице,
      // и захват на ней обрывался бы после первой же перестановки.
      strip.setPointerCapture(event.pointerId);
    });
    return el;
  }

  strip.addEventListener('pointermove', (event) => {
    if (!press) return;
    const el = press.element;
    if (!press.dragging) {
      // Перетаскивать можно только закреплённые программы; остальные иконки просто нажимаются.
      if (!el.classList.contains('pinned-app') || Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD) return;
      press.dragging = true;
      el.classList.add('dragging');
      BD.send({ type: 'dock', action: 'unhover', id: press.id });
      layout(); // на время перетаскивания увеличение выключается
    }
    // Иконка встаёт перед первой закреплённой программой, чей центр правее курсора.
    const others = [...strip.querySelectorAll('.pinned-app')].filter((x) => x !== el);
    const next = others.find((x) => { const r = x.getBoundingClientRect(); return event.clientX < r.left + r.width / 2; });
    const before = next ?? (others.length ? others[others.length - 1].nextSibling : el.nextSibling);
    if (before !== el && before !== el.nextSibling) strip.insertBefore(el, before);
  });

  function endPress(isClick) {
    if (!press) return;
    const { id, element, dragging: wasDragging } = press;
    press = null;
    element.classList.remove('dragging');
    if (wasDragging) {
      BD.send({ type: 'dock', action: 'reorder', ids: [...strip.querySelectorAll('.pinned-app')].map((x) => x.dataset.id) });
      render(); // пока тянули, обновления состава откладывались
    } else if (isClick) {
      BD.send({ type: 'dock', action: 'click', id });
    }
  }
  strip.addEventListener('pointerup', (event) => {
    // Нажатие считается кликом, если кнопку отпустили над той же иконкой.
    const over = document.elementFromPoint(event.clientX, event.clientY);
    endPress(Boolean(press && press.element.contains(over)));
  });
  strip.addEventListener('pointercancel', () => endPress(false));
  strip.addEventListener('lostpointercapture', () => endPress(false));

  function render() {
    if (dragging()) return; // во время перетаскивания порядок на странице главнее присланного
    const seen = new Set();
    for (const item of items) {
      seen.add(item.id);
      let el = elements.get(item.id);
      if (!el) {
        el = createElement(item);
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
  document.documentElement.addEventListener('mouseenter', (event) => setHovered(true, event.clientX));
  document.documentElement.addEventListener('mouseleave', () => { if (!press) setHovered(false); });
  document.addEventListener('mousemove', (event) => {
    if (!hovered) { setHovered(true, event.clientX); return; }
    mouseX = event.clientX;
    if (!frame) frame = requestAnimationFrame(() => { frame = 0; layout(); });
  });
  window.addEventListener('resize', refresh);

  // Страховка: событие ухода курсора иногда теряется (окно меняет форму прямо под ним), и иконки
  // остаются увеличенными. Программа дважды в секунду сообщает, где курсор на самом деле.
  BD.on('pointer', (point) => {
    if (!hovered || press || !clip) return;
    const inside = point.x >= clip.x && point.x < clip.x + clip.width && point.y >= clip.y && point.y < clip.y + clip.height;
    if (!inside) setHovered(false);
  });

  BD.on('dockHold', (data) => { held = data.value; refresh(); });
  BD.on('dock', (data) => { items = data.items; render(); });
  BD.on('dockIcons', (data) => { icons = data; render(); });
  BD.on('state', () => { pendingClip = ''; render(); });
}
