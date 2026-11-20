// Виджеты на рабочем столе: свободная расстановка, режим редактирования, добавление и удаление.
// Сами виджеты регистрируются через BD.widget(...): встроенные лежат в папке widgets/,
// сторонние — в папке пользователя (их подключает shell.js).
if (BD.role === 'desktop') {
  const root = document.getElementById('desktop');
  const palette = document.getElementById('widget-palette');
  const guideX = document.createElement('div');
  const guideY = document.createElement('div');
  guideX.className = 'widget-guide vertical';
  guideY.className = 'widget-guide horizontal';
  root.append(guideX, guideY);

  // Размеры виджетов задаются в «клетках» (так их описывают сами виджеты), а место — свободно, в px страницы.
  const CELL = 84, GAP = 12, MARGIN = 16;
  const BOTTOM_RESERVE = 96;   // место под док внизу экрана
  const SNAP = 8;              // на таком расстоянии виджет прилипает к краям соседей и экрана
  const SPACING = GAP;         // при прилипании к соседу между ними остаётся такой зазор

  let layout = [];             // [{ id, type, w, h, left, top, options }]
  let editing = false;
  let press = null;            // перетаскивание
  let lastClip = '';
  const instances = new Map(); // id -> { element, body, type, api }

  const size = (cells) => cells * CELL + (cells - 1) * GAP;
  const cellLeft = (cells) => MARGIN + cells * (CELL + GAP);
  const box = (i) => ({ left: i.left, top: i.top, right: i.left + size(i.w), bottom: i.top + size(i.h) });
  const area = () => ({ left: MARGIN, top: MARGIN, right: window.innerWidth - MARGIN, bottom: window.innerHeight - BOTTOM_RESERVE });

  const overlaps = (a, b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
  function fits(item) {
    const b = box(item), screen = area();
    if (b.left < screen.left - 0.5 || b.top < screen.top - 0.5 || b.right > screen.right + 0.5 || b.bottom > screen.bottom + 0.5) return false;
    return !layout.some((other) => other.id !== item.id && overlaps(b, box(other)));
  }

  /** Раскладки из прошлых версий хранят место в клетках — переводим в px один раз. */
  function normalize(item) {
    const result = { ...item, options: item.options ?? {} };
    if (result.left == null || result.top == null) {
      result.left = cellLeft(result.x ?? 0);
      result.top = cellLeft(result.y ?? 0);
    }
    return result;
  }

  function save() {
    BD.send({ type: 'widgetsSave', widgets: layout.map(({ id, type, w, h, left, top, options }) => ({ id, type, w, h, left: Math.round(left), top: Math.round(top), options })) });
  }

  /** Окно под виджеты занимает весь рабочий стол; программе сообщается, где виджеты, остальное пропускает клики. */
  function updateClip() {
    const rects = editing
      ? [{ x: 0, y: 0, width: window.innerWidth, height: window.innerHeight }]
      : layout.map((i) => ({ x: i.left, y: i.top, width: size(i.w), height: size(i.h) }));
    const key = JSON.stringify(rects);
    if (key === lastClip) return;
    lastClip = key;
    BD.send({ type: 'clip', rects });
  }

  function place(element, item) {
    element.style.transform = `translate(${item.left}px, ${item.top}px)`;
    element.style.width = `${size(item.w)}px`;
    element.style.height = `${size(item.h)}px`;
    element.dataset.size = `${item.w}x${item.h}`;
  }

  function create(item) {
    const type = BD.widgetTypes[item.type];
    if (!type) return null;
    const element = document.createElement('div');
    element.className = `widget widget-${item.type} entering`;
    const body = document.createElement('div');
    body.className = 'widget-body';

    // Кнопки режима редактирования: сменить размер и убрать.
    const tools = document.createElement('div');
    tools.className = 'widget-tools';
    const resize = document.createElement('button');
    resize.textContent = '⤢';
    resize.title = BD.t('widgets.resize');
    resize.addEventListener('click', () => cycleSize(item.id));
    const remove = document.createElement('button');
    remove.textContent = '✕';
    remove.title = BD.t('widgets.remove');
    remove.addEventListener('click', () => { layout = layout.filter((i) => i.id !== item.id); render(); save(); });
    tools.append(resize, remove);
    // У виджетов с собственными настройками (город для погоды) есть ещё кнопка с карандашом.
    if (type.editable) {
      const edit = document.createElement('button');
      edit.textContent = '✎';
      edit.addEventListener('click', () => instances.get(item.id)?.api.edit?.());
      tools.prepend(edit);
    }
    // Нажатие на кнопку не должно начинать перетаскивание виджета.
    tools.addEventListener('pointerdown', (event) => event.stopPropagation());

    element.append(body, tools);
    element.addEventListener('pointerdown', (event) => startDrag(event, item.id));
    root.appendChild(element);
    // Появление: виджет проявляется и чуть «вырастает» на месте.
    requestAnimationFrame(() => requestAnimationFrame(() => element.classList.remove('entering')));

    const context = {
      id: item.id,
      get options() { return layout.find((i) => i.id === item.id)?.options ?? {}; },
      setOptions(options) {
        const current = layout.find((i) => i.id === item.id);
        if (!current) return;
        current.options = { ...current.options, ...options };
        save();
      },
    };
    let api = {};
    try {
      api = type.create(body, context) ?? {};
    } catch (error) {
      // Сломанный сторонний виджет не должен ронять остальные.
      body.textContent = `${type.title}: ${error.message}`;
      BD.send({ type: 'log', text: `Виджет ${item.type} не создан: ${error.message}` });
    }
    return { element, body, type: item.type, api };
  }

  function render() {
    const seen = new Set();
    for (const item of layout) {
      let instance = instances.get(item.id);
      if (instance && instance.type !== item.type) { destroy(item.id); instance = null; }
      if (!instance) {
        instance = create(item);
        if (!instance) continue;
        instances.set(item.id, instance);
      }
      seen.add(item.id);
      if (press?.item.id !== item.id) place(instance.element, item);
      instance.api.resize?.(item.w, item.h);
    }
    for (const id of [...instances.keys()]) if (!seen.has(id)) destroy(id);

    document.body.classList.toggle('editing', editing);
    palette.classList.toggle('shown', editing);
    if (editing) renderPalette();
    updateClip();
  }

  /** Исчезновение — коротко гаснет и уменьшается, потом удаляется. */
  function destroy(id) {
    const instance = instances.get(id);
    if (!instance) return;
    instances.delete(id);
    instance.api.destroy?.();
    instance.element.classList.add('leaving');
    setTimeout(() => instance.element.remove(), 200);
  }

  // ---------- редактирование ----------

  function renderPalette() {
    palette.replaceChildren();
    for (const [name, type] of Object.entries(BD.widgetTypes)) {
      const button = document.createElement('button');
      button.textContent = `+ ${BD.t(type.title)}`;
      button.addEventListener('click', () => add(name));
      palette.appendChild(button);
    }
    const done = document.createElement('button');
    done.className = 'primary';
    done.textContent = BD.t('widgets.done');
    done.addEventListener('click', () => BD.send({ type: 'widgetsEdit', value: false }));
    palette.appendChild(done);
  }

  /** Новый виджет встаёт на первое свободное место, если смотреть сверху вниз и слева направо. */
  function add(typeName) {
    const [w, h] = BD.widgetTypes[typeName].sizes[0];
    const item = { id: `${typeName}-${Date.now().toString(36)}`, type: typeName, w, h, left: 0, top: 0, options: {} };
    const screen = area(), step = (CELL + GAP) / 2;
    for (let left = screen.left; left + size(w) <= screen.right; left += step) {
      for (let top = screen.top; top + size(h) <= screen.bottom; top += step) {
        Object.assign(item, { left, top });
        if (fits(item)) { layout.push(item); render(); save(); return; }
      }
    }
    BD.send({ type: 'log', text: `Нет места для виджета ${typeName}` });
  }

  /** Следующий из размеров, предусмотренных виджетом, который помещается на этом месте. */
  function cycleSize(id) {
    const item = layout.find((i) => i.id === id);
    const sizes = BD.widgetTypes[item.type].sizes;
    const current = sizes.findIndex(([w, h]) => w === item.w && h === item.h);
    for (let step = 1; step <= sizes.length; step++) {
      const [w, h] = sizes[(current + step) % sizes.length];
      if (fits({ ...item, w, h })) { item.w = w; item.h = h; render(); save(); return; }
    }
  }

  function startDrag(event, id) {
    if (!editing || event.button !== 0) return;
    const item = layout.find((i) => i.id === id);
    const element = instances.get(id).element;
    press = {
      item, element,
      offsetX: event.clientX - item.left, offsetY: event.clientY - item.top,
      valid: { left: item.left, top: item.top }, // последнее место, где виджет ни на кого не налезал
      from: { left: item.left, top: item.top },
    };
    element.classList.add('dragging');
    root.setPointerCapture(event.pointerId);
  }

  /**
   * Прилипание: если край (или центр) виджета близко к краю соседа, краю экрана или центру экрана,
   * он встаёт ровно туда, а на экране появляется направляющая. С зажатым Alt — без прилипания.
   */
  function snap(item, raw, free) {
    const w = size(item.w), h = size(item.h), screen = area();
    const result = { left: raw.left, top: raw.top, guideX: null, guideY: null };
    if (free) return result;

    const xs = [screen.left, screen.right - w, (window.innerWidth - w) / 2];
    const ys = [screen.top, screen.bottom - h, (window.innerHeight - h) / 2];
    const guidesX = [screen.left, screen.right, window.innerWidth / 2];
    const guidesY = [screen.top, screen.bottom, window.innerHeight / 2];
    for (const other of layout) {
      if (other.id === item.id) continue;
      const o = box(other);
      // Встать вплотную (с зазором) слева/справа, сверху/снизу, или выровняться по тому же краю.
      xs.push(o.right + SPACING, o.left - w - SPACING, o.left, o.right - w);
      guidesX.push(o.right + SPACING, o.left - SPACING, o.left, o.right);
      ys.push(o.bottom + SPACING, o.top - h - SPACING, o.top, o.bottom - h);
      guidesY.push(o.bottom + SPACING, o.top - SPACING, o.top, o.bottom);
    }
    const nearest = (value, options) => options.reduce((best, option, index) =>
      Math.abs(option - value) < Math.abs(best.value - value) ? { value: option, index } : best, { value: Infinity, index: -1 });

    const nx = nearest(raw.left, xs);
    if (Math.abs(nx.value - raw.left) <= SNAP) { result.left = nx.value; result.guideX = guidesX[nx.index]; }
    const ny = nearest(raw.top, ys);
    if (Math.abs(ny.value - raw.top) <= SNAP) { result.top = ny.value; result.guideY = guidesY[ny.index]; }
    return result;
  }

  function showGuides(x, y) {
    guideX.classList.toggle('shown', x != null);
    guideY.classList.toggle('shown', y != null);
    if (x != null) guideX.style.transform = `translateX(${x}px)`;
    if (y != null) guideY.style.transform = `translateY(${y}px)`;
  }

  root.addEventListener('pointermove', (event) => {
    if (!press) return;
    const { item, element } = press;
    const screen = area();
    const raw = {
      left: Math.min(Math.max(event.clientX - press.offsetX, screen.left), screen.right - size(item.w)),
      top: Math.min(Math.max(event.clientY - press.offsetY, screen.top), screen.bottom - size(item.h)),
    };
    const snapped = snap(item, raw, event.altKey);
    item.left = snapped.left;
    item.top = snapped.top;
    place(element, item);
    showGuides(snapped.guideX, snapped.guideY);
    // Над чужим виджетом бросить нельзя: подсвечиваем и запоминаем последнее хорошее место.
    const ok = fits(item);
    element.classList.toggle('blocked', !ok);
    if (ok) press.valid = { left: item.left, top: item.top };
  });

  function endDrag() {
    if (!press) return;
    const { item, element, valid, from } = press;
    press = null;
    showGuides(null, null);
    element.classList.remove('dragging', 'blocked');
    // Отпустили над другим виджетом — плавно возвращаемся на последнее свободное место.
    if (!fits(item)) { item.left = valid.left; item.top = valid.top; }
    place(element, item);
    if (item.left !== from.left || item.top !== from.top) save();
  }
  root.addEventListener('pointerup', endDrag);
  root.addEventListener('pointercancel', endDrag);
  root.addEventListener('lostpointercapture', endDrag);

  // ---------- данные ----------

  BD.on('state', (state) => {
    editing = Boolean(state.editing);
    // Пока человек тащит виджет, присланная раскладка не должна сбивать то, что у него под рукой.
    if (!press) layout = (state.widgets ?? []).map(normalize);
    render();
    for (const instance of instances.values()) instance.api.update?.();
  });
  // Сторонние виджеты подгружаются позже встроенных — когда они зарегистрировались, дорисовываем.
  BD.on('widgetTypesChanged', () => render());
  window.addEventListener('resize', () => { lastClip = ''; render(); });

  // Раз в секунду виджеты, которым это нужно (часы), обновляют показания.
  // Пока рабочий стол закрыт окном или открыто полноэкранное приложение — нет.
  setInterval(() => {
    if (BD.state?.paused) return;
    for (const instance of instances.values()) instance.api.update?.();
  }, 1000);
}
