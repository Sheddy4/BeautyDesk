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

  /** Место иконки в состоянии покоя. Меню и превью привязываются к нему, а не к увеличенной под курсором иконке. */
  function restRect(el) {
    const index = [...strip.children].indexOf(el);
    const size = baseSize();
    const top = window.innerHeight - dockMargin() - dockHeight();
    return { x: restGeometry().centers[index] - size / 2, y: top, width: size, height: dockHeight() };
  }

  /** Сообщает программе цвета и шрифт темы — ими красится окно превью, которое рисует не страница. */
  function reportTheme() {
    const style = getComputedStyle(document.documentElement);
    const probe = document.createElement('span');
    document.body.appendChild(probe);
    const color = (name) => {
      probe.style.color = style.getPropertyValue(name).trim();
      return (getComputedStyle(probe).color.match(/[\d.]+/g) ?? [0, 0, 0]).slice(0, 3).map(Number);
    };
    BD.send({
      type: 'themeColors',
      background: color('--bd-menu-bg'),
      foreground: color('--bd-fg'),
      font: style.getPropertyValue('--bd-font').replaceAll('"', '').trim(),
    });
    probe.remove();
  }
  document.getElementById('theme').addEventListener('load', reportTheme);

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

  let lastSurface = '';
  let surfaceUntil = 0;

  /**
   * В темах с «матовым стеклом» под подложку дока программа подкладывает размытие — ей нужно знать,
   * где подложка сейчас. Пока иконки растут или док выезжает, место меняется каждый кадр.
   */
  function reportSurface() {
    let message = { type: 'surface', rect: null };
    if (BD.state?.blur) {
      const r = strip.getBoundingClientRect();
      message = { type: 'surface', rect: { x: r.left, y: r.top, width: r.width, height: r.height } };
    }
    const key = JSON.stringify(message);
    if (key !== lastSurface) { lastSurface = key; BD.send(message); }
    if (performance.now() < surfaceUntil) requestAnimationFrame(reportSurface);
  }

  /** Следить за подложкой ещё какое-то время: столько длятся анимации размера и выезда. */
  function trackSurface(ms = 400) {
    const running = performance.now() < surfaceUntil;
    surfaceUntil = performance.now() + ms;
    if (!running) requestAnimationFrame(reportSurface);
  }

  function refresh() {
    document.body.classList.toggle('dock-hidden', isHidden());
    layout();
    updateClip();
    trackSurface();
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
    el.addEventListener('contextmenu', () => BD.send({ type: 'dock', action: 'menu', id, rect: restRect(el) }));
    el.addEventListener('mouseenter', () => {
      if (!press && el.classList.contains('running')) BD.send({ type: 'dock', action: 'hover', id, rect: restRect(el) });
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
      // Перетаскивать можно программы; папки и корзина просто нажимаются.
      if (el.dataset.kind !== 'app' || Math.abs(event.clientX - press.startX) < DRAG_THRESHOLD) return;
      press.dragging = true;
      el.classList.add('dragging');
      BD.send({ type: 'dock', action: 'unhover', id: press.id });
      layout(); // на время перетаскивания увеличение выключается
    }
    // Иконка встаёт перед первой программой, чей центр правее курсора; если такой нет — в конец программ.
    const others = appElements().filter((x) => x !== el);
    const next = others.find((x) => { const r = x.getBoundingClientRect(); return event.clientX < r.left + r.width / 2; });
    const before = next ?? (others.length ? others[others.length - 1].nextSibling : el.nextSibling);
    if (before !== el && before !== el.nextSibling) strip.insertBefore(el, before);
  });

  const appElements = () => [...strip.children].filter((x) => x.dataset.kind === 'app');

  function endPress(isClick) {
    if (!press) return;
    const { id, element, dragging: wasDragging } = press;
    press = null;
    element.classList.remove('dragging');
    if (wasDragging) {
      BD.send({ type: 'dock', action: 'reorder', id, ids: appElements().map((x) => x.dataset.id) });
      render(); // пока тянули, обновления состава откладывались
    } else if (isClick) {
      // Программа ещё не запущена — иконка подпрыгивает, пока она открывается.
      if (!element.classList.contains('running') && element.dataset.kind === 'app') {
        element.classList.remove('launching');
        void element.offsetWidth;
        element.classList.add('launching');
        setTimeout(() => element.classList.remove('launching'), 1300);
      }
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
        // Новая иконка (запустили программу) вырастает; при первом показе дока — без анимации.
        if (elements.size > items.length - 1 && strip.childElementCount > 0) {
          el.classList.add('appearing');
          setTimeout(() => el.classList.remove('appearing'), 300);
        }
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
        // Если картинки нет (закреплённая программа не запущена, а её файл закрыт для чтения), рисуем плитку с буквой.
        el.classList.toggle('no-icon', !src);
        el.dataset.letter = (title || '?').trim().charAt(0).toUpperCase();
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
    if (!frame) frame = requestAnimationFrame(() => { frame = 0; layout(); trackSurface(200); });
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
  BD.on('state', () => { pendingClip = ''; lastSurface = ''; render(); });
}
