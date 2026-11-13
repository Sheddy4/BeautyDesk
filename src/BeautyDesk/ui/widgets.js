// Виджеты на рабочем столе: расстановка по сетке, режим редактирования, добавление и удаление.
// Сами виджеты лежат в папке widgets/ и регистрируются через BD.widget(...).
if (BD.role === 'desktop') {
  const root = document.getElementById('desktop');
  const palette = document.getElementById('widget-palette');

  // Сетка: клетка и промежуток в px страницы. Размеры виджетов задаются в клетках.
  const CELL = 84, GAP = 12, MARGIN = 16;
  const BOTTOM_RESERVE = 96; // место под док внизу экрана

  let layout = [];              // [{ id, type, x, y, w, h, options }]
  let editing = false;
  let press = null;             // перетаскивание: { item, element, offsetX, offsetY, fromX, fromY }
  let lastClip = '';
  const instances = new Map();  // id -> { element, body, type, api }

  const left = (cells) => MARGIN + cells * (CELL + GAP);
  const size = (cells) => cells * CELL + (cells - 1) * GAP;
  const columns = () => Math.max(1, Math.floor((window.innerWidth - MARGIN * 2 + GAP) / (CELL + GAP)));
  const rows = () => Math.max(1, Math.floor((window.innerHeight - MARGIN - BOTTOM_RESERVE + GAP) / (CELL + GAP)));

  const overlaps = (a, b) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  const fits = (item, except) =>
    item.x >= 0 && item.y >= 0 && item.x + item.w <= columns() && item.y + item.h <= rows() &&
    !layout.some((other) => other !== except && other.id !== item.id && overlaps(item, other));

  function save() {
    BD.send({ type: 'widgetsSave', widgets: layout });
  }

  /** Окно под виджеты занимает весь рабочий стол; программе сообщается, где виджеты, остальное пропускает клики. */
  function updateClip() {
    const rects = editing
      ? [{ x: 0, y: 0, width: window.innerWidth, height: window.innerHeight }]
      : layout.map((i) => ({ x: left(i.x), y: left(i.y), width: size(i.w), height: size(i.h) }));
    const key = JSON.stringify(rects);
    if (key === lastClip) return;
    lastClip = key;
    BD.send({ type: 'clip', rects });
  }

  function place(element, item) {
    element.style.left = `${left(item.x)}px`;
    element.style.top = `${left(item.y)}px`;
    element.style.width = `${size(item.w)}px`;
    element.style.height = `${size(item.h)}px`;
    element.dataset.size = `${item.w}x${item.h}`;
  }

  function create(item) {
    const type = BD.widgetTypes[item.type];
    if (!type) return null;
    const element = document.createElement('div');
    element.className = `widget widget-${item.type}`;
    const body = document.createElement('div');
    body.className = 'widget-body';

    // Кнопки режима редактирования: сменить размер и убрать.
    const tools = document.createElement('div');
    tools.className = 'widget-tools';
    const resize = document.createElement('button');
    resize.textContent = '⤢';
    resize.addEventListener('click', () => cycleSize(item.id));
    const remove = document.createElement('button');
    remove.textContent = '✕';
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
    return { element, body, type: item.type, api: type.create(body, context) ?? {} };
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
      place(instance.element, item);
      instance.api.resize?.(item.w, item.h);
    }
    for (const id of [...instances.keys()]) if (!seen.has(id)) destroy(id);

    document.body.classList.toggle('editing', editing);
    palette.hidden = !editing;
    if (editing) renderPalette();
    updateClip();
  }

  function destroy(id) {
    const instance = instances.get(id);
    if (!instance) return;
    instance.api.destroy?.();
    instance.element.remove();
    instances.delete(id);
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

  /** Новый виджет встаёт на первое свободное место, если смотреть слева направо и сверху вниз. */
  function add(typeName) {
    const [w, h] = BD.widgetTypes[typeName].sizes[0];
    const item = { id: `${typeName}-${Date.now().toString(36)}`, type: typeName, x: 0, y: 0, w, h, options: {} };
    for (item.y = 0; item.y + h <= rows(); item.y++) {
      for (item.x = 0; item.x + w <= columns(); item.x++) {
        if (fits(item)) { layout.push(item); render(); save(); return; }
      }
    }
  }

  /** Следующий из размеров, предусмотренных виджетом, который помещается на этом месте. */
  function cycleSize(id) {
    const item = layout.find((i) => i.id === id);
    const sizes = BD.widgetTypes[item.type].sizes;
    const current = sizes.findIndex(([w, h]) => w === item.w && h === item.h);
    for (let step = 1; step <= sizes.length; step++) {
      const [w, h] = sizes[(current + step) % sizes.length];
      if (fits({ ...item, w, h }, item)) { item.w = w; item.h = h; render(); save(); return; }
    }
  }

  function startDrag(event, id) {
    if (!editing || event.button !== 0) return;
    const item = layout.find((i) => i.id === id);
    const element = instances.get(id).element;
    const box = element.getBoundingClientRect();
    press = { item, element, offsetX: event.clientX - box.left, offsetY: event.clientY - box.top, fromX: item.x, fromY: item.y };
    element.classList.add('dragging');
    root.setPointerCapture(event.pointerId);
  }

  root.addEventListener('pointermove', (event) => {
    if (!press) return;
    const { item, element } = press;
    // Виджет идёт за курсором по клеткам; на занятое место и за край он не встаёт.
    const x = Math.round((event.clientX - press.offsetX - MARGIN) / (CELL + GAP));
    const y = Math.round((event.clientY - press.offsetY - MARGIN) / (CELL + GAP));
    if (fits({ ...item, x, y }, item)) {
      item.x = x;
      item.y = y;
      place(element, item);
    }
  });

  function endDrag() {
    if (!press) return;
    const moved = press.item.x !== press.fromX || press.item.y !== press.fromY;
    press.element.classList.remove('dragging');
    press = null;
    if (moved) save();
  }
  root.addEventListener('pointerup', endDrag);
  root.addEventListener('pointercancel', endDrag);
  root.addEventListener('lostpointercapture', endDrag);

  // ---------- данные ----------

  BD.on('state', (state) => {
    editing = Boolean(state.editing);
    // Пока человек тащит виджет, присланная раскладка не должна сбивать то, что у него под рукой.
    if (!press) layout = (state.widgets ?? []).map((i) => ({ ...i, options: i.options ?? {} }));
    render();
    for (const instance of instances.values()) instance.api.update?.();
  });
  window.addEventListener('resize', () => { lastClip = ''; render(); });

  // Раз в секунду виджеты, которым это нужно (часы), обновляют показания. При полноэкранном приложении — нет.
  setInterval(() => {
    if (BD.state?.paused) return;
    for (const instance of instances.values()) instance.api.update?.();
  }, 1000);
}
